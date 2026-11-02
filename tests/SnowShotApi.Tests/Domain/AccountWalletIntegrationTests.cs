using System.Security.Cryptography;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

[Collection(DistributedIntegrationGroup.Name)]
public sealed class AccountWalletIntegrationTests
{
    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction, 0)]
    [InlineData(UsageKind.LatexExtraction, 0)]
    [InlineData(UsageKind.TableExtraction, 50)]
    [InlineData(UsageKind.LatexExtraction, 50)]
    public async Task MemberRecognitionNeedsNeitherCreditsNorFreeAllowanceAndRetainsOperatorCosts(UsageKind kind, long freeRemaining)
    {
        var f = await SeedAsync(0, freeRemaining, outstanding: 100, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        var reservation = await f.Ledger.ReserveAsync(Operation(f, 100, kind), Token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        Assert.True(handle.Snapshot.MembershipRecognitionFree);
        Assert.Equal(NanoYuan.Zero, handle.Snapshot.PublicReservation);
        var completion = await CompletionAsync(f, handle, 25);
        var settled = await f.Ledger.CompleteAsync(completion, Token);
        Assert.True(settled.Accepted);
        Assert.Equal(NanoYuan.Zero, settled.Decision!.PublicCost);
        Assert.Equal(new NanoYuan(25), settled.Decision.OperatorCost);
        Assert.True((await f.Ledger.CompleteAsync(completion, Token)).Accepted);
        await using var db = new SnowShotDbContext(f.Options);
        var wallet = await db.AccountWallets.SingleAsync(x => x.AccountId == f.Account, Token);
        Assert.Equal(0, wallet.AvailableNanoYuan);
        Assert.Equal(0, wallet.HeldNanoYuan);
        Assert.Equal(100, wallet.OutstandingNanoYuan);
        Assert.Equal(MembershipRules.FreeRecognitionThresholdNanoYuan, wallet.CumulativeRechargeNanoYuan);
        var allowance = await db.AllowancePeriods.SingleAsync(x => x.PrincipalId == f.Principal, Token);
        Assert.Equal(f.Policy.PrincipalDailyAllowance.Value - freeRemaining, allowance.CommittedNanoYuan);
        Assert.Equal(0, allowance.ReservedNanoYuan);
        Assert.Empty(await db.AccountFinancialEntries.Where(x => x.AccountId == f.Account).ToListAsync(Token));
        var usage = await db.UsageEvents.SingleAsync(x => x.OperationId == handle.OperationId, Token);
        Assert.Equal(0, usage.PublicCostNanoYuan);
        Assert.Equal(25, usage.OperatorCostNanoYuan);
        Assert.All(await db.OperatorBudgetPeriods.ToListAsync(Token), budget => Assert.True(budget.CommittedNanoYuan >= 25));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction)]
    [InlineData(UsageKind.LatexExtraction)]
    public async Task RefundChangesOnlyNewRecognitionRequestsAndRecoveryKeepsOriginalBenefit(UsageKind kind)
    {
        var f = await SeedAsync(200, 0, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        var operation = Operation(f, 100, kind);
        var handle = (await f.Ledger.ReserveAsync(operation, Token)).Handle!;
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountWallets.Where(x => x.AccountId == f.Account).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.CumulativeRechargeNanoYuan, MembershipRules.FreeRecognitionThresholdNanoYuan - MembershipRules.NanoYuanPerFen), Token);
        var recovered = await f.Ledger.ReserveAsync(operation, Token);
        Assert.True(recovered.Accepted);
        Assert.Equal(handle.OperationId, recovered.Handle!.OperationId);
        Assert.True(recovered.Handle.Snapshot.MembershipRecognitionFree);
        Assert.Equal(NanoYuan.Zero, (await f.Ledger.CompleteAsync(await CompletionAsync(f, recovered.Handle, 25), Token)).Decision!.PublicCost);
        var next = await f.Ledger.ReserveAsync(Operation(f, 100, kind), Token);
        Assert.True(next.Accepted);
        Assert.False(next.Handle!.Snapshot.MembershipRecognitionFree);
        Assert.Equal(new NanoYuan(100), next.Handle.Snapshot.PublicReservation);
        await using var check = new SnowShotDbContext(f.Options);
        var wallet = await check.AccountWallets.SingleAsync(x => x.AccountId == f.Account, Token);
        Assert.Equal(100, wallet.AvailableNanoYuan);
        Assert.Equal(100, wallet.HeldNanoYuan);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction)]
    [InlineData(UsageKind.LatexExtraction)]
    public async Task UpgradeDoesNotRepriceAnAlreadyReservedPaidRequest(UsageKind kind)
    {
        var f = await SeedAsync(200, 0, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan - 1);
        var operation = Operation(f, 100, kind);
        Assert.True((await f.Ledger.ReserveAsync(operation, Token)).Accepted);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountWallets.Where(x => x.AccountId == f.Account).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.CumulativeRechargeNanoYuan, MembershipRules.FreeRecognitionThresholdNanoYuan), Token);
        var recovered = await f.Ledger.ReserveAsync(operation, Token);
        Assert.True(recovered.Accepted);
        Assert.False(recovered.Handle!.Snapshot.MembershipRecognitionFree);
        Assert.Equal(new NanoYuan(100), (await f.Ledger.CompleteAsync(await CompletionAsync(f, recovered.Handle, 100), Token)).Decision!.PublicCost);
        Assert.True((await f.Ledger.ReserveAsync(Operation(f, 100, kind), Token)).Handle!.Snapshot.MembershipRecognitionFree);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.Chat)]
    [InlineData(UsageKind.Translation)]
    public async Task MemberBenefitDoesNotWaiveChatOrTranslation(UsageKind kind)
    {
        var f = await SeedAsync(100, 0, cumulative: 99900 * MembershipRules.NanoYuanPerFen);
        var result = await f.Ledger.ReserveAsync(Operation(f, 100, kind), Token);
        Assert.True(result.Accepted);
        Assert.False(result.Handle!.Snapshot.MembershipRecognitionFree);
        Assert.Equal(new NanoYuan(100), result.Handle.Snapshot.PublicReservation);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Equal(100, await db.AccountWallets.Where(x => x.AccountId == f.Account).Select(x => x.HeldNanoYuan).SingleAsync(Token));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction)]
    [InlineData(UsageKind.LatexExtraction)]
    public async Task AnonymousRecognitionStillRequiresFreeAllowance(UsageKind kind)
    {
        var f = await SeedAsync(0, 0, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        var result = await f.Ledger.ReserveAsync(Operation(f, 100, kind) with { AccountId = null }, Token);
        Assert.False(result.Accepted);
        Assert.Equal(ReservationRejectionReason.AllowanceExhausted, result.RejectionReason);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction)]
    [InlineData(UsageKind.LatexExtraction)]
    public async Task MemberRecognitionStillRequiresOperatorBudget(UsageKind kind)
    {
        var f = await SeedAsync(0, 0, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        var operation = Operation(f, 100, kind);
        operation = operation with { Snapshot = operation.Snapshot with { OperatorMaximum = new(f.Policy.DailyOperatorBudget.Value + 1) } };
        var result = await f.Ledger.ReserveAsync(operation, Token);
        Assert.False(result.Accepted);
        Assert.Equal(ReservationRejectionReason.OperatorBudgetExhausted, result.RejectionReason);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction)]
    [InlineData(UsageKind.LatexExtraction)]
    public async Task ExpiredMemberRecognitionRetainsZeroCustomerCost(UsageKind kind)
    {
        var f = await SeedAsync(0, 0, outstanding: 100, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        var handle = (await f.Ledger.ReserveAsync(Operation(f, 100, kind), Token)).Handle!;
        await f.Ledger.MarkDispatchedAsync(handle, TimeSpan.FromSeconds(30), Token);
        await f.Ledger.PrepareAttemptAsync(new(Guid.CreateVersion7(), handle, 1, "test", handle.Snapshot.Resource, DateTimeOffset.UtcNow), Token);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.UsageOperations.Where(x => x.Id == handle.OperationId).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Token);
        Assert.True(await f.Ledger.ReconcileExpiredAsync(100, Token) > 0);
        await using var check = new SnowShotDbContext(f.Options);
        var usage = await check.UsageEvents.SingleAsync(x => x.OperationId == handle.OperationId, Token);
        Assert.Equal(0, usage.PublicCostNanoYuan);
        Assert.Equal(100, usage.OperatorCostNanoYuan);
        Assert.Empty(await check.AccountFinancialEntries.Where(x => x.AccountId == f.Account).ToListAsync(Token));
        var wallet = await check.AccountWallets.SingleAsync(x => x.AccountId == f.Account, Token);
        Assert.Equal(0, wallet.HeldNanoYuan);
        Assert.Equal(100, wallet.OutstandingNanoYuan);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(UsageKind.TableExtraction)]
    [InlineData(UsageKind.LatexExtraction)]
    public async Task LostMemberReservationCommitAcknowledgementRecoversPersistedBenefit(UsageKind kind)
    {
        var f = await SeedAsync(0, 0, cumulative: MembershipRules.FreeRecognitionThresholdNanoYuan);
        var fault = new LostMemberCommit(f);
        var options = new DbContextOptionsBuilder<SnowShotDbContext>(f.Options)
            .UseNpgsql(Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!, pg => pg
                .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema).EnableRetryOnFailure(3))
            .AddInterceptors(fault).Options;
        var ledger = new PostgresOperationLedger(new DistributedIntegrationTests.ContextFactory(options), f.Policy);
        var result = await ledger.ReserveAsync(Operation(f, 100, kind), Token);
        Assert.True(result.Accepted);
        Assert.True(result.Handle!.Snapshot.MembershipRecognitionFree);
        Assert.Equal(NanoYuan.Zero, result.Handle.Snapshot.PublicReservation);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Equal(0, await db.AccountWallets.Where(x => x.AccountId == f.Account).Select(x => x.CumulativeRechargeNanoYuan).SingleAsync(Token));
        Assert.Single(await db.UsageOperations.Where(x => x.AccountId == f.Account).ToListAsync(Token));
        Assert.Single(await db.AccountUsageDeduplications.Where(x => x.AccountId == f.Account).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentClientsCannotReserveTheSamePurchasedFunds()
    {
        var fixture = await SeedAsync(100, 0);
        var otherPrincipal = Guid.CreateVersion7();
        await using (var setup = new SnowShotDbContext(fixture.Options))
        {
            var source = await setup.AllowancePeriods.SingleAsync(value => value.PrincipalId == fixture.Principal, Token);
            setup.Principals.Add(new PrincipalEntity { Id = otherPrincipal, CreatedAt = DateTimeOffset.UtcNow });
            setup.AllowancePeriods.Add(new AllowancePeriodEntity
            {
                PrincipalId = otherPrincipal,
                PeriodDate = source.PeriodDate,
                LimitNanoYuan = source.LimitNanoYuan,
                CommittedNanoYuan = source.CommittedNanoYuan,
                AppliedPolicyRevision = source.AppliedPolicyRevision,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await setup.SaveChangesAsync(Token);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(index => fixture.Ledger.ReserveAsync(Operation(index % 2 == 0 ? fixture : fixture with { Principal = otherPrincipal }, 40), Token)));
        Assert.Equal(2, results.Count(result => result.Accepted));
        await using var context = new SnowShotDbContext(fixture.Options);
        var wallet = await context.AccountWallets.SingleAsync(value => value.AccountId == fixture.Account, Token);
        Assert.Equal(20, wallet.AvailableNanoYuan);
        Assert.Equal(80, wallet.HeldNanoYuan);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MixedFreeAndPurchasedSettlementIsAtomicAndIdempotent()
    {
        var fixture = await SeedAsync(80, 20);
        var reservation = await fixture.Ledger.ReserveAsync(Operation(fixture, 100), Token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        var completion = await CompletionAsync(fixture, handle, 65);
        Assert.True((await fixture.Ledger.CompleteAsync(completion, Token)).Accepted);
        Assert.True((await fixture.Ledger.CompleteAsync(completion, Token)).Accepted);
        await using var context = new SnowShotDbContext(fixture.Options);
        var wallet = await context.AccountWallets.SingleAsync(value => value.AccountId == fixture.Account, Token);
        Assert.Equal(35, wallet.AvailableNanoYuan);
        Assert.Equal(0, wallet.HeldNanoYuan);
        var entry = await context.AccountFinancialEntries.SingleAsync(value => value.AccountId == fixture.Account, Token);
        Assert.Equal(-45, entry.AmountNanoYuan);
        var allowance = await context.AllowancePeriods.SingleAsync(value => value.PrincipalId == fixture.Principal, Token);
        Assert.Equal(fixture.Policy.PrincipalDailyAllowance.Value, allowance.CommittedNanoYuan);
        Assert.Equal(0, allowance.ReservedNanoYuan);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RefundDebtCannotConsumeAnInflightHoldAndUnusedFundsRepayDebt()
    {
        var fixture = await SeedAsync(100, 0);
        var reservation = await fixture.Ledger.ReserveAsync(Operation(fixture, 100), Token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        await using (var context = new SnowShotDbContext(fixture.Options))
        {
            var wallet = await context.AccountWallets.SingleAsync(value => value.AccountId == fixture.Account, Token);
            var refunded = AccountRules.Refund(wallet.AvailableNanoYuan, wallet.OutstandingNanoYuan, 75);
            wallet.AvailableNanoYuan = refunded.Available;
            wallet.OutstandingNanoYuan = refunded.Outstanding;
            await context.SaveChangesAsync(Token);
        }
        var completion = await CompletionAsync(fixture, handle, 60);
        Assert.True((await fixture.Ledger.CompleteAsync(completion, Token)).Accepted);
        await using var check = new SnowShotDbContext(fixture.Options);
        var remaining = await check.AccountWallets.SingleAsync(value => value.AccountId == fixture.Account, Token);
        Assert.Equal(0, remaining.AvailableNanoYuan);
        Assert.Equal(0, remaining.HeldNanoYuan);
        Assert.Equal(35, remaining.OutstandingNanoYuan);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DebtKeepsFullyFundedFreeCallsAvailable()
    {
        var fixture = await SeedAsync(100, 50, outstanding: 100);
        Assert.True((await fixture.Ledger.ReserveAsync(Operation(fixture, 50), Token)).Accepted);
        var paid = await fixture.Ledger.ReserveAsync(Operation(fixture, 1), Token);
        Assert.False(paid.Accepted);
        Assert.Equal(ReservationRejectionReason.OutstandingBalance, paid.RejectionReason);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UnexpectedProviderOverageCannotOverdrawWallet()
    {
        var fixture = await SeedAsync(100, 0);
        var reservation = await fixture.Ledger.ReserveAsync(Operation(fixture, 100), Token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        var completion = await CompletionAsync(fixture, handle, 150);
        var settled = await fixture.Ledger.CompleteAsync(completion, Token);
        Assert.True(settled.Accepted);
        Assert.Equal(100, settled.Decision!.PublicCost.Value);
        await using var check = new SnowShotDbContext(fixture.Options);
        var wallet = await check.AccountWallets.SingleAsync(value => value.AccountId == fixture.Account, Token);
        Assert.Equal(0, wallet.AvailableNanoYuan);
        Assert.Equal(0, wallet.HeldNanoYuan);
        Assert.Equal(0, wallet.OutstandingNanoYuan);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DeduplicationSurvivesOperationalRetention()
    {
        var fixture = await SeedAsync(200, 0);
        var operation = Operation(fixture, 100);
        var reservation = await fixture.Ledger.ReserveAsync(operation, Token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        Assert.True((await fixture.Ledger.CompleteAsync(await CompletionAsync(fixture, handle, 50), Token)).Accepted);
        await using (var context = new SnowShotDbContext(fixture.Options))
        {
            await context.ProviderAttempts.Where(value => value.OperationId == operation.Id).ExecuteDeleteAsync(Token);
            await context.UsageEvents.Where(value => value.OperationId == operation.Id).ExecuteDeleteAsync(Token);
            await context.UsageOperations.Where(value => value.Id == operation.Id).ExecuteDeleteAsync(Token);
        }
        var repeated = await fixture.Ledger.ReserveAsync(operation with { Id = Guid.CreateVersion7() }, Token);
        Assert.False(repeated.Accepted);
        Assert.Equal(ReservationRejectionReason.DuplicateRequest, repeated.RejectionReason);
        await using var check = new SnowShotDbContext(fixture.Options);
        Assert.Single(await check.AccountFinancialEntries.Where(value => value.AccountId == fixture.Account).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExpiredUsageLeaseReleasesPurchasedReservation()
    {
        var fixture = await SeedAsync(100, 0);
        var operation = Operation(fixture, 100);
        Assert.True((await fixture.Ledger.ReserveAsync(operation, Token)).Accepted);
        await using (var context = new SnowShotDbContext(fixture.Options))
            await context.UsageOperations.Where(value => value.Id == operation.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Token);
        Assert.True(await fixture.Ledger.ReconcileExpiredAsync(100, Token) > 0);
        await using var check = new SnowShotDbContext(fixture.Options);
        var wallet = await check.AccountWallets.SingleAsync(value => value.AccountId == fixture.Account, Token);
        Assert.Equal(100, wallet.AvailableNanoYuan);
        Assert.Equal(0, wallet.HeldNanoYuan);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Fixture(DbContextOptions<SnowShotDbContext> Options, Guid Account, Guid Principal,
        ServicePolicy Policy, PostgresOperationLedger Ledger);

    private static async Task<Fixture> SeedAsync(long available, long freeRemaining, long outstanding = 0, long cumulative = 0)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Set SNOWSHOT_RUN_INTEGRATION=true for isolated PostgreSQL account tests.");
        var options = DistributedIntegrationTests.DatabaseOptions(Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!);
        var account = Guid.CreateVersion7();
        var principal = Guid.CreateVersion7();
        var policy = ServicePolicy.Defaults();
        await using var context = new SnowShotDbContext(options);
        var date = await context.Database.SqlQuery<DateOnly>($"""
            SELECT (clock_timestamp() AT TIME ZONE 'Asia/Shanghai')::date AS "Value"
            """).SingleAsync(Token);
        context.Principals.Add(new PrincipalEntity { Id = principal, CreatedAt = DateTimeOffset.UtcNow });
        context.Accounts.Add(new AccountEntity
        {
            Id = account,
            Username = "test_" + account.ToString("N")[..12],
            NormalizedUsername = "TEST_" + account.ToString("N")[..12],
            PasswordHash = "test-not-a-password-hash",
            Active = true,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.AccountWallets.Add(new AccountWalletEntity { AccountId = account, AvailableNanoYuan = available, OutstandingNanoYuan = outstanding, CumulativeRechargeNanoYuan = cumulative });
        context.AllowancePeriods.Add(new AllowancePeriodEntity
        {
            PrincipalId = principal,
            PeriodDate = date,
            LimitNanoYuan = policy.PrincipalDailyAllowance.Value,
            CommittedNanoYuan = policy.PrincipalDailyAllowance.Value - freeRemaining,
            AppliedPolicyRevision = policy.Revision,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync(Token);
        return new(options, account, principal, policy, new(new DistributedIntegrationTests.ContextFactory(options), policy));
    }

    private static ReserveOperation Operation(Fixture fixture, long charge, UsageKind kind = UsageKind.Chat)
    {
        var resource = fixture.Policy.Get(kind switch
        {
            UsageKind.TableExtraction => Resources.TableExtraction,
            UsageKind.LatexExtraction => Resources.LatexExtraction,
            UsageKind.Translation => Resources.Translation,
            _ => Resources.QwenFlash,
        });
        return new(Guid.CreateVersion7(), fixture.Principal, kind,
            new(fixture.Policy.Revision, fixture.Policy.Fingerprint, resource.Resource, resource.Price,
                fixture.Policy.PrincipalDailyAllowance, new(charge), new(100), StrictPublicReservation: true),
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30))
        { AccountId = fixture.Account };
    }

    private static async Task<OperationCompletion> CompletionAsync(Fixture fixture, OperationHandle handle, long actual)
    {
        Assert.Equal(OwnershipMutationResult.Applied, await fixture.Ledger.MarkDispatchedAsync(handle, TimeSpan.FromSeconds(30), Token));
        var now = DateTimeOffset.UtcNow;
        var preparation = new ProviderAttemptPreparation(Guid.CreateVersion7(), handle, 1, "test", handle.Snapshot.Resource, now);
        Assert.Equal(OwnershipMutationResult.Applied, await fixture.Ledger.PrepareAttemptAsync(preparation, Token));
        var attempt = new ProviderAttempt(preparation.Id, handle.OperationId, 1, "test", handle.Snapshot.Resource,
            "success", 200, 1, 1, new(actual), CostBasis.Exact, AttemptDispatchState.Dispatched, now, now);
        return new(new(handle, new(actual), new(actual), true, CostBasis.Exact, true, 1, 1, "success"), attempt);
    }

    private sealed class LostMemberCommit(Fixture fixture) : DbTransactionInterceptor
    {
        private int _armed = 1;

        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0) return;
            // The commit succeeded. A concurrent refund removes eligibility before
            // the reservation execution strategy can recover its lost response.
            await using var db = new SnowShotDbContext(fixture.Options);
            await db.AccountWallets.Where(x => x.AccountId == fixture.Account).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.CumulativeRechargeNanoYuan, 0L), cancellationToken);
            throw new NpgsqlException("Synthetic lost member reservation acknowledgement.", new TimeoutException());
        }
    }
}
