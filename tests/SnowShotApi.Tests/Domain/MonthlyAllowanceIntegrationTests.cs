using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Identity;
using SnowShot.Infrastructure.Operations;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed class MonthlyAllowanceIntegrationTests : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string _database = "monthly_allowance_" + Guid.NewGuid().ToString("N");
    private NpgsqlConnection? _admin;
    private DbContextOptions<SnowShotDbContext>? _options;
    private string? _connectionString;
    private bool _created;

    private DbContextOptions<SnowShotDbContext> Options
    {
        get
        {
            if (_options is null) Assert.Skip("Enable the isolated PostgreSQL integration fixture.");
            return _options!;
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task EachIpHasIndependentLimitsSharedBySignedInAndAnonymousRequests()
    {
        var policy = AiModelTests.Configuration().Policy;
        var ledger = await ActivateAsync(policy);
        var identity = new HmacPrincipalIdentity(new DistributedIntegrationTests.ContextFactory(Options),
            new IdentityOptions { HmacKeyBase64 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) });
        var principal = Assert.IsType<AnonymousPrincipal>(await identity.ResolveAsync("203.0.113.10", Token));
        var mapped = Assert.IsType<AnonymousPrincipal>(await identity.ResolveAsync("::ffff:203.0.113.10", Token));
        var other = Assert.IsType<AnonymousPrincipal>(await identity.ResolveAsync("203.0.113.11", Token));
        Assert.Equal(principal.Id, mapped.Id);
        var account = await AddAccountAsync(0, debt: 50);

        foreach (var (kind, resource, limit) in new[]
        {
            (UsageKind.TableExtraction, Resources.TableExtraction, 10),
            (UsageKind.LatexExtraction, Resources.LatexExtraction, 10),
            (UsageKind.Chat, Resources.AiModel, 5),
        })
        {
            for (var index = 0; index < limit; index++)
            {
                var operation = Operation(policy, principal.Id, kind, resource, index == 0 ? account : null);
                var handle = await ReserveAsync(ledger, operation);
                var settled = await ledger.CompleteAsync(await CompletionAsync(ledger, handle), Token);
                Assert.True(settled.Accepted);
                Assert.Equal(0, settled.Decision!.PublicCost.Value);
                Assert.Equal(10, settled.Decision.OperatorCost.Value);
            }

            var exhausted = await ledger.ReserveAsync(Operation(policy, mapped.Id, kind, resource), Token);
            Assert.Equal(ReservationRejectionReason.AllowanceExhausted, exhausted.RejectionReason);
            var now = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
            var reset = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset).AddMonths(1);
            Assert.InRange((exhausted.RetryAfter!.Value - (reset - now)).Duration(), TimeSpan.Zero, TimeSpan.FromSeconds(5));
            await ReserveAsync(ledger, Operation(policy, other.Id, kind, resource));
        }

        await using var db = new SnowShotDbContext(Options);
        Assert.Equal(25, await db.MonthlyFreeRequestPeriods.Where(x => x.PrincipalId == principal.Id).SumAsync(x => x.CommittedRequests, Token));
        Assert.Equal(3, await db.MonthlyFreeRequestPeriods.Where(x => x.PrincipalId == other.Id).SumAsync(x => x.ReservedRequests, Token));
        Assert.Empty(await db.AllowancePeriods.ToListAsync(Token));
        Assert.Empty(await db.AccountFinancialEntries.ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task HoldsAreAtomicAndFailuresAndExpiryReleaseThem()
    {
        var policy = AiModelTests.Configuration().Policy;
        var first = await ActivateAsync(policy);
        var second = Ledger(policy, Options);
        var principal = await AddPrincipalAsync();
        var account = await AddAccountAsync(0);
        var requests = Enumerable.Range(0, 8).Select(_ => Operation(policy, principal)).ToArray();
        var results = await Task.WhenAll(requests.Select((request, index) =>
            (index % 2 == 0 ? first : second).ReserveAsync(request, Token)));
        var accepted = requests.Zip(results).Where(pair => pair.Second.Accepted).ToArray();
        Assert.Equal(5, accepted.Length);
        Assert.All(results.Where(result => !result.Accepted), result =>
        {
            Assert.Equal(ReservationRejectionReason.AllowanceExhausted, result.RejectionReason);
            Assert.Equal(TimeSpan.FromSeconds(2), result.RetryAfter);
        });
        var linked = await first.ReserveAsync(Operation(policy, principal, account: account), Token);
        Assert.Equal(ReservationRejectionReason.FundsTemporarilyReserved, linked.RejectionReason);

        var (original, reservation) = accepted[0];
        var recovered = await ReserveAsync(second, original);
        Assert.Equal(reservation.Handle!.OperationId, recovered.OperationId);
        var duplicate = await second.ReserveAsync(original with { Id = Guid.CreateVersion7() }, Token);
        Assert.Equal(ReservationRejectionReason.DuplicateRequest, duplicate.RejectionReason);
        var failure = await CompletionAsync(first, recovered, delivered: false);
        Assert.True((await first.CompleteAsync(failure, Token)).Accepted);
        Assert.True((await second.CompleteAsync(failure, Token)).Accepted);

        var success = await CompletionAsync(first, accepted[1].Second.Handle!);
        Assert.True((await first.CompleteAsync(success, Token)).Accepted);
        Assert.True((await second.CompleteAsync(success, Token)).Accepted);
        var acknowledgementLoss = new DistributedIntegrationTests.CommitAcknowledgementLossInterceptor();
        var retrying = Ledger(policy, DistributedIntegrationTests.FaultOptions(_connectionString!, acknowledgementLoss));
        acknowledgementLoss.Arm();
        var replacement = await ReserveAsync(retrying, Operation(policy, principal));
        await using var db = new SnowShotDbContext(Options);
        await db.UsageOperations.Where(x => x.Id == replacement.OperationId)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Token);
        Assert.Equal(1, await second.ReconcileExpiredAsync(100, Token));
        Assert.Equal(0, await second.ReconcileExpiredAsync(100, Token));

        var period = await db.MonthlyFreeRequestPeriods.AsNoTracking().SingleAsync(Token);
        Assert.Equal(1, period.CommittedRequests);
        Assert.Equal(3, period.ReservedRequests);
        var overBudget = Operation(policy, principal);
        overBudget = overBudget with { Snapshot = overBudget.Snapshot with { OperatorMaximum = policy.DailyOperatorBudget + new NanoYuan(1) } };
        Assert.Equal(ReservationRejectionReason.OperatorBudgetExhausted, (await second.ReserveAsync(overBudget, Token)).RejectionReason);
        await ReserveAsync(second, Operation(policy, principal));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task WalletFallbackAndMembershipSettleWithoutQuotaConsumption()
    {
        var policy = AiModelTests.Configuration().Policy;
        var ledger = await ActivateAsync(policy);
        var principal = await AddPrincipalAsync();
        var account = await AddAccountAsync(500);
        var member = await AddAccountAsync(0, debt: 100, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        await using var db = new SnowShotDbContext(Options);
        var now = await db.Database.SqlQuery<DateOnly>($"SELECT (clock_timestamp() AT TIME ZONE 'Asia/Shanghai')::date AS \"Value\"").SingleAsync(Token);
        db.MonthlyFreeRequestPeriods.Add(new()
        {
            PrincipalId = principal,
            Resource = Resources.TableExtraction,
            MonthStart = new(now.Year, now.Month, 1),
            CommittedRequests = 10,
        });
        await db.SaveChangesAsync(Token);

        var paidRequests = new[]
        {
            Operation(policy, principal, UsageKind.TableExtraction, Resources.TableExtraction, account),
            Operation(policy, principal, UsageKind.Translation, Resources.Translation, account),
            Operation(policy, principal, UsageKind.Chat, Resources.ExpertModel, account),
            Operation(policy, principal, account: account) with { RequestedResource = Resources.QwenFlash },
        };
        foreach (var paid in paidRequests)
        {
            var handle = await ReserveAsync(ledger, paid);
            var result = await ledger.CompleteAsync(await CompletionAsync(ledger, handle), Token);
            Assert.True(result.Accepted);
            Assert.Equal(60, result.Decision!.PublicCost.Value);
        }

        foreach (var (kind, resource) in new[]
        {
            (UsageKind.TableExtraction, Resources.TableExtraction),
            (UsageKind.LatexExtraction, Resources.LatexExtraction),
        })
        {
            var handle = await ReserveAsync(ledger, Operation(policy, principal, kind, resource, member));
            Assert.True(handle.Snapshot.MembershipRecognitionFree);
            var result = await ledger.CompleteAsync(await CompletionAsync(ledger, handle), Token);
            Assert.True(result.Accepted);
            Assert.Equal(0, result.Decision!.PublicCost.Value);
        }

        var wallet = await db.AccountWallets.SingleAsync(x => x.AccountId == account, Token);
        Assert.Equal(260, wallet.AvailableNanoYuan);
        Assert.Equal(0, wallet.HeldNanoYuan);
        Assert.Equal(4, await db.AccountFinancialEntries.CountAsync(Token));
        var counts = await db.MonthlyFreeRequestPeriods.AsNoTracking().ToDictionaryAsync(x => x.Resource, Token);
        Assert.Equal(2, counts.Count);
        Assert.Equal(10, counts[Resources.TableExtraction].CommittedRequests);
        Assert.Equal(0, counts[Resources.LatexExtraction].CommittedRequests);
        Assert.All(counts.Values, period => Assert.Equal(0, period.ReservedRequests));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MonthBoundaryUsesReservationMonthAndRetentionProtectsOutstandingHolds()
    {
        var policy = AiModelTests.Configuration().Policy;
        await ActivateAsync(policy);
        var principal = await AddPrincipalAsync();
        var current = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(8));
        var newMonth = new DateOnly(current.Year, current.Month, 1);
        var previous = newMonth.AddDays(-1);
        var before = Ledger(policy, WithDate(previous));
        var after = Ledger(policy, WithDate(newMonth));
        for (var index = 0; index < 4; index++)
        {
            var handle = await ReserveAsync(before, Operation(policy, principal));
            Assert.True((await before.CompleteAsync(await CompletionAsync(before, handle), Token)).Accepted);
        }
        var crossing = await ReserveAsync(before, Operation(policy, principal));
        await ReserveAsync(after, Operation(policy, principal));
        Assert.True((await after.CompleteAsync(await CompletionAsync(after, crossing), Token)).Accepted);

        await using var db = new SnowShotDbContext(Options);
        var periods = await db.MonthlyFreeRequestPeriods.AsNoTracking().OrderBy(x => x.MonthStart).ToListAsync(Token);
        Assert.Collection(periods,
            old => { Assert.Equal(newMonth.AddMonths(-1), old.MonthStart); Assert.Equal(5, old.CommittedRequests); Assert.Equal(0, old.ReservedRequests); },
            next => { Assert.Equal(newMonth, next.MonthStart); Assert.Equal(0, next.CommittedRequests); Assert.Equal(1, next.ReservedRequests); });

        var heldMonth = newMonth.AddMonths(-3);
        var removedMonth = newMonth.AddMonths(-4);
        db.MonthlyFreeRequestPeriods.AddRange(
            new() { PrincipalId = principal, Resource = Resources.AiModel, MonthStart = heldMonth, ReservedRequests = 1 },
            new() { PrincipalId = principal, Resource = Resources.AiModel, MonthStart = removedMonth, CommittedRequests = 5 });
        await db.SaveChangesAsync(Token);
        await RetentionService.ApplyCoreAsync(db,
            new RetentionOptions { OperationDays = 1, AggregateDays = 1, IdentityDays = 1 }, 100, Token);
        var retained = await db.MonthlyFreeRequestPeriods.AsNoTracking().Select(x => x.MonthStart).ToListAsync(Token);
        Assert.Contains(newMonth, retained);
        Assert.Contains(heldMonth, retained);
        Assert.DoesNotContain(removedMonth, retained);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MigrationKeepsAnInflightLegacyReservationSettleable()
    {
        var legacyPolicy = ServicePolicy.Defaults();
        var legacy = await ActivateAsync(legacyPolicy);
        var principal = await AddPrincipalAsync();
        var handle = await ReserveAsync(legacy, Operation(legacyPolicy, principal, UsageKind.TableExtraction, Resources.TableExtraction));
        var completion = await CompletionAsync(legacy, handle);
        await using var db = new SnowShotDbContext(Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261111092431_RetainConflictingPaymentClosures", Token);
        await migrator.MigrateAsync(cancellationToken: Token);

        var monthlyPolicy = AiModelTests.Configuration().Policy;
        var ledger = await ActivateAsync(monthlyPolicy);
        var settled = await ledger.CompleteAsync(completion, Token);
        Assert.True(settled.Accepted);
        Assert.Equal(60, settled.Decision!.PublicCost.Value);
        Assert.Equal(60, await db.AllowancePeriods.Select(x => x.CommittedNanoYuan).SingleAsync(Token));
        Assert.Empty(await db.MonthlyFreeRequestPeriods.ToListAsync(Token));
        await ReserveAsync(ledger, Operation(monthlyPolicy, principal, UsageKind.TableExtraction, Resources.TableExtraction));
        Assert.Equal(1, await db.MonthlyFreeRequestPeriods.Select(x => x.ReservedRequests).SingleAsync(Token));
    }

    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!);
        _admin = new NpgsqlConnection(connection.ConnectionString);
        await _admin.OpenAsync(Token);
        await using (var create = new NpgsqlCommand($"CREATE DATABASE {_database}", _admin))
            await create.ExecuteNonQueryAsync(Token);
        _created = true;
        connection.Database = _database;
        connection.Pooling = false;
        _connectionString = connection.ConnectionString;
        _options = DistributedIntegrationTests.DatabaseOptions(_connectionString);
        await using var db = new SnowShotDbContext(Options);
        await db.Database.MigrateAsync(Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (_admin is null) return;
        await using var admin = _admin;
        if (_created)
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {_database} WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<PostgresOperationLedger> ActivateAsync(ServicePolicy policy)
    {
        var factory = new DistributedIntegrationTests.ContextFactory(Options);
        await new PostgresPolicyRegistry(factory, policy, NullLogger<PostgresPolicyRegistry>.Instance).ActivateAsync(Token);
        return new(factory, policy);
    }

    private static PostgresOperationLedger Ledger(ServicePolicy policy, DbContextOptions<SnowShotDbContext> options) =>
        new(new DistributedIntegrationTests.ContextFactory(options), policy);

    private DbContextOptions<SnowShotDbContext> WithDate(DateOnly date) =>
        new DbContextOptionsBuilder<SnowShotDbContext>(DistributedIntegrationTests.DatabaseOptions(_connectionString!))
            .AddInterceptors(new DistributedIntegrationTests.FixedAllowanceDate(date)).Options;

    private async Task<Guid> AddPrincipalAsync()
    {
        var id = Guid.CreateVersion7();
        await using var db = new SnowShotDbContext(Options);
        db.Principals.Add(new() { Id = id, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(Token);
        return id;
    }

    private async Task<Guid> AddAccountAsync(long credit, long debt = 0, long cumulative = 0)
    {
        var id = Guid.CreateVersion7();
        await using var db = new SnowShotDbContext(Options);
        var name = "quota_" + id.ToString("N")[..12];
        db.Accounts.Add(new()
        {
            Id = id, Username = name, NormalizedUsername = name.ToUpperInvariant(),
            PasswordHash = "unused", Active = true, CreatedAt = DateTimeOffset.UtcNow,
        });
        db.AccountWallets.Add(new() { AccountId = id, AvailableNanoYuan = credit, OutstandingNanoYuan = debt, CumulativeRechargeNanoYuan = cumulative });
        await db.SaveChangesAsync(Token);
        return id;
    }

    private static ReserveOperation Operation(ServicePolicy policy, Guid principal, UsageKind kind = UsageKind.Chat,
        string resource = Resources.AiModel, Guid? account = null) => new(Guid.CreateVersion7(), principal, kind,
            new(policy.Revision, policy.Fingerprint, resource, policy.Get(resource).Price,
                policy.PrincipalDailyAllowance, new(100), new(100), StrictPublicReservation: account.HasValue),
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30))
        { AccountId = account };

    private static async Task<OperationHandle> ReserveAsync(PostgresOperationLedger ledger, ReserveOperation operation)
    {
        var result = await ledger.ReserveAsync(operation, Token);
        Assert.True(result.Accepted, $"Reservation rejected: {result.RejectionReason}");
        return Assert.IsType<OperationHandle>(result.Handle);
    }

    private static async Task<OperationCompletion> CompletionAsync(PostgresOperationLedger ledger, OperationHandle handle, bool delivered = true)
    {
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.MarkDispatchedAsync(handle, TimeSpan.FromSeconds(30), Token));
        var now = DateTimeOffset.UtcNow;
        var preparation = new ProviderAttemptPreparation(Guid.CreateVersion7(), handle, 1, "test", handle.Snapshot.Resource, now);
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.PrepareAttemptAsync(preparation, Token));
        var outcome = delivered ? "success" : "failed";
        var attempt = new ProviderAttempt(preparation.Id, handle.OperationId, 1, "test", handle.Snapshot.Resource,
            outcome, delivered ? 200 : 502, 1, 1, new(10), CostBasis.Exact, AttemptDispatchState.Dispatched, now, now);
        return new(new(handle, new(60), new(10), delivered, CostBasis.Exact, true, 1, 1, outcome), attempt);
    }

}
