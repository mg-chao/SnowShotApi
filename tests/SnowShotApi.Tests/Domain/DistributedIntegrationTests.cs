using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Admission;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Identity;
using SnowShot.Infrastructure.Operations;
using SnowShot.Infrastructure.Persistence;
using SnowShot.Infrastructure.Providers;
using StackExchange.Redis;

namespace SnowShotApi.Tests.Domain;

[Collection(DistributedIntegrationGroup.Name)]
public sealed class DistributedIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task PolicyActivationIsDatabaseAuthoredAndIdempotent()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var options = DatabaseOptions(Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!);
        var factory = new ContextFactory(options);
        var policy = ServicePolicy.Defaults();
        var registry = new PostgresPolicyRegistry(factory, policy,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresPolicyRegistry>.Instance);

        await registry.ActivateAsync(token);

        await using var context = new SnowShotDbContext(options);
        var revision = await context.PolicyRevisions.AsNoTracking().SingleAsync(value => value.Revision == policy.Revision, token);
        var state = await context.PolicyStates.AsNoTracking().SingleAsync(value => value.Id == 1, token);
        var databaseNow = await context.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);
        Assert.Equal(policy.Revision, state.ActiveRevision);
        Assert.Equal(policy.Fingerprint, Convert.ToHexString(revision.Fingerprint).ToLowerInvariant());
        Assert.InRange(revision.ActivatedAt, databaseNow.AddMinutes(-5), databaseNow);
        Assert.Equal(1, await context.PolicyRevisions.CountAsync(value => value.Revision == policy.Revision, token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ReservationConvergesWhenCommitSucceedsButAcknowledgementIsLost()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var baseOptions = DatabaseOptions(connectionString);
        var principalId = Guid.CreateVersion7();
        await using (var arrange = new SnowShotDbContext(baseOptions))
        {
            arrange.Principals.Add(new PrincipalEntity { Id = principalId, CreatedAt = DateTimeOffset.UtcNow });
            await arrange.SaveChangesAsync(token);
        }

        var acknowledgementLoss = new CommitAcknowledgementLossInterceptor();
        var faultOptions = new DbContextOptionsBuilder<SnowShotDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)
                .EnableRetryOnFailure(3))
            .AddInterceptors(acknowledgementLoss)
            .Options;
        var policy = ServicePolicy.Defaults();
        var operation = Operation(principalId, policy);
        acknowledgementLoss.Arm();

        var reservation = await new PostgresOperationLedger(new ContextFactory(faultOptions), policy)
            .ReserveAsync(operation, token);

        Assert.True(reservation.Accepted);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        Assert.Equal(operation.Id, handle.OperationId);
        Assert.Equal(policy.Revision, handle.Snapshot.PolicyRevision);
        await using var verification = new SnowShotDbContext(baseOptions);
        Assert.Equal(1, await verification.UsageOperations.CountAsync(
            value => value.IdempotencyHash == operation.IdempotencyHash, token));
        Assert.Equal(policy.Revision, await verification.UsageOperations
            .Where(value => value.Id == operation.Id).Select(value => value.PolicyRevision).SingleAsync(token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task AttemptPreparationConvergesWhenCommitSucceedsButAcknowledgementIsLost()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var baseOptions = DatabaseOptions(connectionString);
        var acknowledgementLoss = new CommitAcknowledgementLossInterceptor();
        var policy = ServicePolicy.Defaults();
        var principalId = await AddPrincipalAsync(baseOptions, token);
        var ledger = new PostgresOperationLedger(
            new ContextFactory(FaultOptions(connectionString, acknowledgementLoss)), policy);
        var handle = await ReserveDispatchedAsync(ledger, Operation(principalId, policy), token);
        var preparation = new ProviderAttemptPreparation(
            Guid.CreateVersion7(), handle, 1, "provider", Resources.QwenFlash, DateTimeOffset.UtcNow);
        acknowledgementLoss.Arm();

        var result = await ledger.PrepareAttemptAsync(preparation, token);

        Assert.Equal(OwnershipMutationResult.Applied, result);
        await using var verification = new SnowShotDbContext(baseOptions);
        var persisted = await verification.ProviderAttempts.AsNoTracking()
            .SingleAsync(value => value.Id == preparation.Id, token);
        Assert.Equal(ProviderAttemptState.Prepared, persisted.State);
        Assert.Equal(AttemptDispatchState.Prepared, persisted.DispatchState);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SettlementConvergesWhenCommitSucceedsButAcknowledgementIsLost()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var baseOptions = DatabaseOptions(connectionString);
        var acknowledgementLoss = new CommitAcknowledgementLossInterceptor();
        var faultOptions = FaultOptions(connectionString, acknowledgementLoss);
        var policy = ServicePolicy.Defaults();
        var principalId = await AddPrincipalAsync(baseOptions, token);
        var ledger = new PostgresOperationLedger(new ContextFactory(faultOptions), policy);
        var handle = await ReserveDispatchedAsync(ledger, Operation(principalId, policy), token);
        var started = DateTimeOffset.UtcNow;
        var preparation = new ProviderAttemptPreparation(Guid.CreateVersion7(), handle, 1,
            "provider", Resources.QwenFlash, started);
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.PrepareAttemptAsync(preparation, token));
        var attempt = new ProviderAttempt(preparation.Id, handle.OperationId, 1, "provider", Resources.QwenFlash,
            "success", 200, 10, 2, new(80), true, AttemptDispatchState.Dispatched,
            started, DateTimeOffset.UtcNow);
        var settlement = new OperationSettlement(handle, new(80), NanoYuan.Zero,
            true, true, false, 10, 2, "success");
        acknowledgementLoss.Arm();

        var result = await ledger.CompleteAsync(new(settlement, attempt), token);

        Assert.True(result.Accepted);
        Assert.Equal(new NanoYuan(80), result.Decision!.OperatorCost);
        await using var verification = new SnowShotDbContext(baseOptions);
        var operation = await verification.UsageOperations.AsNoTracking()
            .SingleAsync(value => value.Id == handle.OperationId, token);
        Assert.Equal(ReservationState.Committed, operation.State);
        Assert.Equal(1, await verification.ProviderAttempts.CountAsync(value => value.OperationId == handle.OperationId, token));
        Assert.Equal(1, await verification.UsageEvents.CountAsync(value => value.OperationId == handle.OperationId, token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ReconciliationConvergesWhenCommitSucceedsButAcknowledgementIsLost()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var baseOptions = DatabaseOptions(connectionString);
        var acknowledgementLoss = new CommitAcknowledgementLossInterceptor();
        var faultOptions = FaultOptions(connectionString, acknowledgementLoss);
        var policy = ServicePolicy.Defaults();
        var principalId = await AddPrincipalAsync(baseOptions, token);
        var ledger = new PostgresOperationLedger(new ContextFactory(faultOptions), policy);
        var reservation = await ledger.ReserveAsync(Operation(principalId, policy), token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        await using (var expire = new SnowShotDbContext(baseOptions))
        {
            await expire.UsageOperations.Where(value => value.Id == handle.OperationId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), token);
        }
        acknowledgementLoss.Arm();

        Assert.Equal(1, await ledger.ReconcileExpiredAsync(1, token));

        await using var verification = new SnowShotDbContext(baseOptions);
        var operation = await verification.UsageOperations.AsNoTracking()
            .SingleAsync(value => value.Id == handle.OperationId, token);
        Assert.Equal(ReservationState.Released, operation.State);
        Assert.Equal(0, operation.ActualOperatorNanoYuan);
        Assert.Equal(1, await verification.UsageEvents.CountAsync(value => value.OperationId == handle.OperationId, token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PostgreSqlSerializesDuplicateAndSettlementRaces()
    {
        SkipUnlessEnabled();
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var options = DatabaseOptions(connectionString);
        var factory = new ContextFactory(options);
        var policy = ServicePolicy.Defaults();
        var ledger = new PostgresOperationLedger(factory, policy);
        var now = DateTimeOffset.UtcNow;
        var principalId = Guid.CreateVersion7();
        await using (var context = new SnowShotDbContext(options))
        {
            context.Principals.Add(new PrincipalEntity
            {
                Id = principalId,
                CreatedAt = now,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var operation = Operation(principalId, policy);
        var duplicate = operation with { Id = Guid.CreateVersion7() };
        var first = ledger.ReserveAsync(operation, TestContext.Current.CancellationToken);
        var second = ledger.ReserveAsync(duplicate, TestContext.Current.CancellationToken);
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, value => value.Accepted);
        Assert.Single(results, value => !value.Accepted && value.RejectionReason == ReservationRejectionReason.DuplicateRequest);

        var accepted = results.Single(value => value.Accepted);
        var committedOperation = results[0].Accepted ? operation : duplicate;
        var handle = Assert.IsType<OperationHandle>(accepted.Handle);
        var recovered = await ledger.ReserveAsync(committedOperation, TestContext.Current.CancellationToken);
        Assert.True(recovered.Accepted);
        Assert.Equal(handle.OperationId, recovered.Handle!.OperationId);
        Assert.Equal(handle.OwnerToken, recovered.Handle.OwnerToken);
        var staleFence = new OperationHandle(handle.OperationId, handle.OwnerToken.AsSpan(), handle.Fence + 1,
            handle.AbsoluteDeadline, handle.Snapshot);
        Assert.Equal(OwnershipMutationResult.LeaseLost,
            await ledger.MarkDispatchedAsync(staleFence, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        Assert.Equal(OwnershipMutationResult.Applied,
            await ledger.MarkDispatchedAsync(handle, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var settlement = new OperationSettlement(handle, new(10), new(10), true, true, false, 5, 5, "success");
        var staleSettlement = await ledger.CompleteAsync(new(settlement with { Handle = staleFence }), TestContext.Current.CancellationToken);
        Assert.Equal(SettlementRejectionReason.LeaseLost, staleSettlement.RejectionReason);
        var decision = await ledger.CompleteAsync(new(settlement), TestContext.Current.CancellationToken);
        var repeated = await ledger.CompleteAsync(new(settlement), TestContext.Current.CancellationToken);
        Assert.True(decision.Accepted);
        Assert.Equal(decision.Decision!.Fingerprint, repeated.Decision!.Fingerprint);
        var conflict = await ledger.CompleteAsync(new(settlement with { Outcome = "conflict" }), TestContext.Current.CancellationToken);
        Assert.Equal(SettlementRejectionReason.Conflict, conflict.RejectionReason);
        await using var verification = new SnowShotDbContext(options);
        Assert.Equal(1, await verification.UsageEvents.CountAsync(value => value.OperationId == handle.OperationId, TestContext.Current.CancellationToken));

        var expiring = Operation(principalId, policy) with { LeaseTtl = TimeSpan.FromMilliseconds(100) };
        var expiringReservation = await ledger.ReserveAsync(expiring, TestContext.Current.CancellationToken);
        var expiringHandle = Assert.IsType<OperationHandle>(expiringReservation.Handle);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        Assert.Equal(OwnershipMutationResult.LeaseLost,
            await ledger.RenewAsync(expiringHandle, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        var expiredSettlement = await ledger.CompleteAsync(new(new(expiringHandle, NanoYuan.Zero, NanoYuan.Zero,
            false, true, false, 0, 0, "late_owner")), TestContext.Current.CancellationToken);
        Assert.Equal(SettlementRejectionReason.LeaseLost, expiredSettlement.RejectionReason);
        Assert.True(await ledger.ReconcileExpiredAsync(100, TestContext.Current.CancellationToken) >= 1);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PostgreSqlPersistsAttemptCheckpointsAndReconcilesFromDurableCertainty()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var options = DatabaseOptions(connectionString);
        var factory = new ContextFactory(options);
        var policy = ServicePolicy.Defaults();
        var ledger = new PostgresOperationLedger(factory, policy);
        var principalId = Guid.CreateVersion7();
        await using (var context = new SnowShotDbContext(options))
        {
            context.Principals.Add(new PrincipalEntity { Id = principalId, CreatedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync(token);
        }

        var operation = Operation(principalId, policy);
        var reservation = await ledger.ReserveAsync(operation, token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        Assert.Equal(OwnershipMutationResult.Applied,
            await ledger.MarkDispatchedAsync(handle, TimeSpan.FromSeconds(30), token));

        var firstStarted = DateTimeOffset.UtcNow;
        var firstPreparation = new ProviderAttemptPreparation(Guid.CreateVersion7(), handle, 1,
            "provider", Resources.QwenFlash, firstStarted);
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.PrepareAttemptAsync(firstPreparation, token));
        await using (var checkpoint = new SnowShotDbContext(options))
        {
            var prepared = await checkpoint.ProviderAttempts.AsNoTracking().SingleAsync(value => value.Id == firstPreparation.Id, token);
            Assert.Equal(ProviderAttemptState.Prepared, prepared.State);
            Assert.Null(prepared.CompletedAt);
        }

        var firstAttempt = new ProviderAttempt(firstPreparation.Id, handle.OperationId, 1, "provider", Resources.QwenFlash,
            "retryable_failure", 503, 10, 0, new(10), true, AttemptDispatchState.Dispatched,
            firstStarted, DateTimeOffset.UtcNow);
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.CompleteAttemptAsync(handle, firstAttempt, token));
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.CompleteAttemptAsync(handle, firstAttempt, token));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ledger.CompleteAttemptAsync(handle, firstAttempt with { Outcome = "conflicting_replay" }, token));

        var secondStarted = DateTimeOffset.UtcNow;
        var secondPreparation = new ProviderAttemptPreparation(Guid.CreateVersion7(), handle, 2,
            "provider", Resources.QwenFlash, secondStarted);
        Assert.Equal(OwnershipMutationResult.Applied, await ledger.PrepareAttemptAsync(secondPreparation, token));
        var secondAttempt = new ProviderAttempt(secondPreparation.Id, handle.OperationId, 2, "provider", Resources.QwenFlash,
            "success", 200, 20, 0, new(20), true, AttemptDispatchState.Dispatched,
            secondStarted, DateTimeOffset.UtcNow);
        var settlement = new OperationSettlement(handle, new(30), NanoYuan.Zero, true, true, false, 30, 0, "success");
        var staleHandle = new OperationHandle(handle.OperationId, handle.OwnerToken.AsSpan(), handle.Fence + 1,
            handle.AbsoluteDeadline, handle.Snapshot);
        var stale = await ledger.CompleteAsync(new(settlement with { Handle = staleHandle }, secondAttempt), token);
        Assert.Equal(SettlementRejectionReason.LeaseLost, stale.RejectionReason);
        await using (var rolledBack = new SnowShotDbContext(options))
            Assert.Equal(ProviderAttemptState.Prepared,
                (await rolledBack.ProviderAttempts.AsNoTracking().SingleAsync(value => value.Id == secondPreparation.Id, token)).State);

        var completed = await ledger.CompleteAsync(new(settlement, secondAttempt), token);
        Assert.True(completed.Accepted);
        Assert.Equal(new NanoYuan(30), completed.Decision!.OperatorCost);
        var replayed = await ledger.CompleteAsync(new(settlement, secondAttempt), token);
        Assert.True(replayed.Accepted);
        Assert.Equal(completed.Decision.Fingerprint, replayed.Decision!.Fingerprint);

        var exact = await ReserveDispatchedAsync(ledger, Operation(principalId, policy), token);
        var exactStarted = DateTimeOffset.UtcNow;
        var exactPreparation = new ProviderAttemptPreparation(Guid.CreateVersion7(), exact, 1,
            "provider", Resources.QwenFlash, exactStarted);
        await ledger.PrepareAttemptAsync(exactPreparation, token);
        await ledger.CompleteAttemptAsync(exact, new ProviderAttempt(exactPreparation.Id, exact.OperationId, 1,
            "provider", Resources.QwenFlash, "completed_before_crash", 200, 7, 0, new(7), true,
            AttemptDispatchState.Dispatched, exactStarted, DateTimeOffset.UtcNow), token);

        var uncertain = await ReserveDispatchedAsync(ledger, Operation(principalId, policy), token);
        await ledger.PrepareAttemptAsync(new ProviderAttemptPreparation(Guid.CreateVersion7(), uncertain, 1,
            "provider", Resources.QwenFlash, DateTimeOffset.UtcNow), token);
        await using (var expire = new SnowShotDbContext(options))
        {
            var expiredAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            await expire.UsageOperations.Where(value => value.Id == exact.OperationId || value.Id == uncertain.OperationId)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.LeaseExpiresAt, expiredAt), token);
        }

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => ledger.ReconcileExpiredAsync(2, token)));
        await using (var verification = new SnowShotDbContext(options))
        {
            var exactOperation = await verification.UsageOperations.AsNoTracking().SingleAsync(value => value.Id == exact.OperationId, token);
            var uncertainOperation = await verification.UsageOperations.AsNoTracking().SingleAsync(value => value.Id == uncertain.OperationId, token);
            Assert.True(exactOperation.State.IsTerminal());
            Assert.True(uncertainOperation.State.IsTerminal());
            Assert.Equal(7, exactOperation.ActualOperatorNanoYuan);
            Assert.Equal(uncertain.Snapshot.OperatorMaximum.Value, uncertainOperation.ActualOperatorNanoYuan);
            Assert.True((await verification.UsageEvents.AsNoTracking().SingleAsync(value => value.OperationId == exact.OperationId, token)).CostKnown);
            Assert.False((await verification.UsageEvents.AsNoTracking().SingleAsync(value => value.OperationId == uncertain.OperationId, token)).CostKnown);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RedisEnforcesExactQueueAndRenewableOwnership()
    {
        SkipUnlessEnabled();
        var redisConnection = Environment.GetEnvironmentVariable("ConnectionStrings__Redis")!;
        await using var redis = await ConnectionMultiplexer.ConnectAsync(redisConnection);
        var limiter = new RedisAdmissionController(redis);
        var resource = $"integration-{Guid.NewGuid():N}";
        var activeDeltas = new ConcurrentQueue<long>();
        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SnowShot" && instrument.Name == "snowshot.leases.active")
                listener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "resource" && string.Equals(tag.Value as string, resource, StringComparison.Ordinal))
                    activeDeltas.Enqueue(measurement);
            }
        });
        meterListener.Start();
        var policy = new AdmissionPolicy(100, 1, 1, 1, TimeSpan.FromSeconds(5));
        AdmissionRequest Request(string principal) => new(resource, principal, policy,
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(2));
        await using var first = await limiter.AcquireAsync(Request("first"), TestContext.Current.CancellationToken);
        var secondTask = limiter.AcquireAsync(Request("second"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await using var rejected = await limiter.AcquireAsync(Request("third"), TestContext.Current.CancellationToken);
        Assert.Equal(AdmissionRejectionReason.QueueFull, rejected.RejectionReason);
        Assert.True(await first.RenewAsync(TestContext.Current.CancellationToken));
        await first.ReleaseAsync(TestContext.Current.CancellationToken);
        await using var second = await secondTask;
        Assert.True(second.Acquired);
        await second.ReleaseAsync(TestContext.Current.CancellationToken);

        var shortRequest = Request("expired") with { LeaseTtl = TimeSpan.FromMilliseconds(100) };
        await using var expired = await limiter.AcquireAsync(shortRequest, TestContext.Current.CancellationToken);
        await Task.Delay(150, TestContext.Current.CancellationToken);
        await using var replacement = await limiter.AcquireAsync(Request("replacement"), TestContext.Current.CancellationToken);
        Assert.True(replacement.Acquired);
        Assert.False(await expired.RenewAsync(TestContext.Current.CancellationToken));
        Assert.True(expired.OwnershipLost.IsCancellationRequested);
        await replacement.ReleaseAsync(TestContext.Current.CancellationToken);
        Assert.Contains(1, activeDeltas);
        Assert.Contains(-1, activeDeltas);
        Assert.Equal(0, activeDeltas.Sum());

        var fifoResource = $"integration-fifo-{Guid.NewGuid():N}";
        var fifoPolicy = new AdmissionPolicy(100, 1, 1, 2, TimeSpan.FromSeconds(5))
        { PerPrincipalQueueLength = 1 };
        AdmissionRequest FifoRequest(string principal, TimeSpan? wait = null, TimeSpan? ttl = null) =>
            new(fifoResource, principal, fifoPolicy, wait ?? TimeSpan.FromSeconds(5), ttl ?? TimeSpan.FromSeconds(2));
        await using var blocker = await limiter.AcquireAsync(FifoRequest("blocker"), TestContext.Current.CancellationToken);
        var firstQueuedTask = limiter.AcquireAsync(FifoRequest("queued-first"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var secondQueuedTask = limiter.AcquireAsync(FifoRequest("queued-second"), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        await using var principalQueueRejected = await limiter.AcquireAsync(FifoRequest("queued-first"), TestContext.Current.CancellationToken);
        Assert.Equal(AdmissionRejectionReason.QueueFull, principalQueueRejected.RejectionReason);
        await blocker.ReleaseAsync(TestContext.Current.CancellationToken);
        await using var firstQueued = await firstQueuedTask;
        Assert.True(firstQueued.Acquired);
        Assert.False(secondQueuedTask.IsCompleted);
        await firstQueued.ReleaseAsync(TestContext.Current.CancellationToken);
        await using var secondQueued = await secondQueuedTask;
        Assert.True(secondQueued.Acquired);
        await secondQueued.ReleaseAsync(TestContext.Current.CancellationToken);

        var staleResource = $"integration-stale-{Guid.NewGuid():N}";
        var staleTag = $"{{snowshot:{staleResource}}}";
        var staleTicket = Guid.NewGuid().ToString("N");
        var stalePrincipal = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("dead-principal"))).ToLowerInvariant();
        var staleDeadline = DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds();
        var database = redis.GetDatabase();
        await database.SortedSetAddAsync($"{staleTag}:queue:order", staleTicket, 1);
        await database.SortedSetAddAsync($"{staleTag}:queue:expiry", staleTicket, staleDeadline);
        await database.HashSetAsync($"{staleTag}:queue:metadata", staleTicket, $"{stalePrincipal}|{staleDeadline}");
        await database.HashSetAsync($"{staleTag}:queue:principal-counts", stalePrincipal, 1);
        var stalePolicy = new AdmissionPolicy(100, 1, 1, 1, TimeSpan.FromSeconds(2));
        await using var afterStale = await limiter.AcquireAsync(
            new(staleResource, "live-principal", stalePolicy, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2)),
            TestContext.Current.CancellationToken);
        Assert.True(afterStale.Acquired);
        Assert.Equal(0, await database.SortedSetLengthAsync($"{staleTag}:queue:order"));
        Assert.False(await database.HashExistsAsync($"{staleTag}:queue:metadata", staleTicket));
        await afterStale.ReleaseAsync(TestContext.Current.CancellationToken);

        var mixedResource = $"integration-mixed-{Guid.NewGuid():N}";
        var mixedPolicy = new AdmissionPolicy(100, 1, 2, 2, TimeSpan.FromSeconds(5));
        await using var longLease = await limiter.AcquireAsync(
            new(mixedResource, "long-lease", mixedPolicy, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);
        await using var shortLease = await limiter.AcquireAsync(
            new(mixedResource, "short-lease", mixedPolicy, TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(500)),
            TestContext.Current.CancellationToken);
        var activeTtl = await database.KeyTimeToLiveAsync($"{{snowshot:{mixedResource}}}:active");
        Assert.NotNull(activeTtl);
        Assert.True(activeTtl > TimeSpan.FromSeconds(4));
        await longLease.ReleaseAsync(TestContext.Current.CancellationToken);
        await shortLease.ReleaseAsync(TestContext.Current.CancellationToken);

        var mixedQueueResource = $"integration-mixed-queue-{Guid.NewGuid():N}";
        await using var queueBlocker = await limiter.AcquireAsync(
            new(mixedQueueResource, "blocker", fifoPolicy, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)),
            TestContext.Current.CancellationToken);
        using var longQueueCancellation = new CancellationTokenSource();
        using var shortQueueCancellation = new CancellationTokenSource();
        var longQueue = limiter.AcquireAsync(
            new(mixedQueueResource, "long-wait", fifoPolicy, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5)), longQueueCancellation.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var shortQueue = limiter.AcquireAsync(
            new(mixedQueueResource, "short-wait", fifoPolicy, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), shortQueueCancellation.Token);
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var queueTtl = await database.KeyTimeToLiveAsync($"{{snowshot:{mixedQueueResource}}}:queue:order");
        Assert.NotNull(queueTtl);
        Assert.True(queueTtl > TimeSpan.FromSeconds(4));
        longQueueCancellation.Cancel();
        shortQueueCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => longQueue);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => shortQueue);
        await queueBlocker.ReleaseAsync(TestContext.Current.CancellationToken);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RetentionSkipsWhenAnotherReplicaOwnsTheAdvisoryLock()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var options = DatabaseOptions(connectionString);
        await using var lockOwner = new SnowShotDbContext(options);
        await using var transaction = await lockOwner.Database.BeginTransactionAsync(token);
        await lockOwner.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(764219683101)", token);
        await using var contender = new SnowShotDbContext(options);

        var removed = await RetentionService.ApplyCoreAsync(
            contender,
            new RetentionOptions { OperationDays = 90, AggregateDays = 400, IdentityDays = 400 },
            100,
            token);

        Assert.Equal(RetentionSweepResult.Empty, removed);
        await transaction.RollbackAsync(token);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RetentionBoundsEveryCategoryAndRemovesOnlyUnreferencedOldIdentities()
    {
        SkipUnlessEnabled();
        var token = TestContext.Current.CancellationToken;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var options = DatabaseOptions(connectionString);
        var oldPrincipalId = Guid.CreateVersion7();
        var recentPrincipalId = Guid.CreateVersion7();
        var old = DateTimeOffset.UtcNow.AddDays(-500);
        var recent = DateTimeOffset.UtcNow;
        var oldDate = DateOnly.FromDateTime(old.UtcDateTime);

        await using (var arrange = new SnowShotDbContext(options))
        {
            arrange.Principals.AddRange(
                new PrincipalEntity { Id = oldPrincipalId, CreatedAt = old },
                new PrincipalEntity { Id = recentPrincipalId, CreatedAt = recent });
            arrange.PrincipalFingerprints.AddRange(
                new PrincipalFingerprintEntity
                {
                    Fingerprint = SHA256.HashData(oldPrincipalId.ToByteArray()),
                    PrincipalId = oldPrincipalId,
                    CreatedAt = old,
                    LastSeenAt = old,
                },
                new PrincipalFingerprintEntity
                {
                    Fingerprint = SHA256.HashData(recentPrincipalId.ToByteArray()),
                    PrincipalId = recentPrincipalId,
                    CreatedAt = recent,
                    LastSeenAt = recent,
                });
            arrange.AllowancePeriods.Add(new AllowancePeriodEntity
            {
                PrincipalId = oldPrincipalId,
                PeriodDate = oldDate,
                LimitNanoYuan = 1,
                AppliedPolicyRevision = 1,
                UpdatedAt = old,
            });
            arrange.OperatorBudgetPeriods.Add(new OperatorBudgetPeriodEntity
            {
                Kind = BudgetPeriodKind.Daily,
                PeriodKey = oldDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture),
                LimitNanoYuan = 1,
                AppliedPolicyRevision = 1,
                UpdatedAt = old,
            });
            arrange.DailyAggregates.Add(new DailyAggregateEntity
            {
                UsageDate = oldDate,
                Kind = UsageKind.Chat,
                Resource = $"retention-{oldPrincipalId:N}",
                UpdatedAt = old,
            });
            await arrange.SaveChangesAsync(token);
        }

        RetentionSweepResult removed;
        await using (var sweep = new SnowShotDbContext(options))
        {
            removed = await RetentionService.ApplyCoreAsync(sweep,
                new RetentionOptions { OperationDays = 90, AggregateDays = 400, IdentityDays = 400 },
                1,
                token);
        }

        Assert.Equal(1, removed.Aggregates);
        Assert.Equal(1, removed.AllowancePeriods);
        Assert.Equal(1, removed.BudgetPeriods);
        Assert.Equal(1, removed.Fingerprints);
        Assert.Equal(1, removed.Principals);
        Assert.True(removed.HasFullCategory(1));
        await using var verification = new SnowShotDbContext(options);
        Assert.False(await verification.Principals.AnyAsync(value => value.Id == oldPrincipalId, token));
        Assert.True(await verification.Principals.AnyAsync(value => value.Id == recentPrincipalId, token));
        Assert.True(await verification.PrincipalFingerprints.AnyAsync(value => value.PrincipalId == recentPrincipalId, token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task HmacRotationAndMixedReplicasConvergeOnOneStablePrincipal()
    {
        SkipUnlessEnabled();
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var options = DatabaseOptions(connectionString);
        var factory = new ContextFactory(options);
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        var oldReplica = new HmacPrincipalIdentity(factory, Identity(k1));
        var rotatingReplica = new HmacPrincipalIdentity(factory, Identity(k2, k1));
        const string address = "203.0.113.197";

        var original = Assert.IsType<AnonymousPrincipal>(
            await oldReplica.ResolveAsync(address, TestContext.Current.CancellationToken));
        var concurrent = Enumerable.Range(0, 12).Select(index => (index & 1) == 0
            ? oldReplica.ResolveAsync(address, TestContext.Current.CancellationToken)
            : rotatingReplica.ResolveAsync(address, TestContext.Current.CancellationToken));
        var principals = await Task.WhenAll(concurrent);

        Assert.All(principals, principal => Assert.Equal(original.Id, principal!.Id));
        await using var verification = new SnowShotDbContext(options);
        Assert.Equal(2, await verification.PrincipalFingerprints.CountAsync(
            value => value.PrincipalId == original.Id, TestContext.Current.CancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConflictingIdentityAliasesFailClosed()
    {
        SkipUnlessEnabled();
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")!;
        var options = DatabaseOptions(connectionString);
        var factory = new ContextFactory(options);
        var k1 = RandomNumberGenerator.GetBytes(32);
        var k2 = RandomNumberGenerator.GetBytes(32);
        const string address = "203.0.113.198";
        var canonical = Encoding.ASCII.GetBytes(address);
        var now = DateTimeOffset.UtcNow;
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await using (var context = new SnowShotDbContext(options))
        {
            context.Principals.AddRange(
                new PrincipalEntity { Id = first, CreatedAt = now },
                new PrincipalEntity { Id = second, CreatedAt = now });
            context.PrincipalFingerprints.AddRange(
                new PrincipalFingerprintEntity
                {
                    Fingerprint = HMACSHA256.HashData(k1, canonical),
                    PrincipalId = first,
                    CreatedAt = now,
                    LastSeenAt = now,
                },
                new PrincipalFingerprintEntity
                {
                    Fingerprint = HMACSHA256.HashData(k2, canonical),
                    PrincipalId = second,
                    CreatedAt = now,
                    LastSeenAt = now,
                });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }
        var resolver = new HmacPrincipalIdentity(factory, Identity(k2, k1));

        await Assert.ThrowsAsync<IdentityIntegrityException>(() =>
            resolver.ResolveAsync(address, TestContext.Current.CancellationToken));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RedisProviderPoolBalancesAcrossReplicasAndCapsEachAccess()
    {
        SkipUnlessEnabled();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("ConnectionStrings__Redis")!);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var accessA = $"integration-a-{suffix}";
        var accessB = $"integration-b-{suffix}";
        ProviderModelOptions Model(string upstream) => new()
        {
            Accesses = new Dictionary<string, ProviderAccessOptions>(StringComparer.Ordinal)
            {
                [accessA] = new() { Provider = "one", UpstreamModel = upstream, MaxConcurrentRequests = 16 },
                [accessB] = new() { Provider = "two", UpstreamModel = upstream, MaxConcurrentRequests = 16 },
            },
        };
        var catalog = new ProviderModelCatalog(new ProviderModelsOptions
        {
            CloudProviders = new Dictionary<string, CloudProviderOptions>(StringComparer.Ordinal)
            {
                ["one"] = new() { Endpoint = "https://one.test/chat", ApiKey = "a" },
                ["two"] = new() { Endpoint = "https://two.test/chat", ApiKey = "b" },
            },
            Models = new Dictionary<string, ProviderModelOptions>(StringComparer.Ordinal)
            {
                [Resources.QwenFlash] = Model("flash"),
                [Resources.QwenPlus] = Model("plus"),
                [Resources.QwenVisionFlash] = Model("vision"),
            },
        }, new TranslationProviderOptions { LogicalModels = [Resources.QwenFlash] }, requireHttps: true);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisProviderAccessPool>.Instance;
        var circuits = new RedisProviderCircuitRegistry(redis, new ProviderCircuitOptions());
        var firstReplica = new RedisProviderAccessPool(catalog, redis, circuits, logger);
        var secondReplica = new RedisProviderAccessPool(catalog, redis, circuits, logger);
        var request = new ProviderAccessRequest(Resources.QwenFlash, new HashSet<string>(StringComparer.Ordinal),
            TimeSpan.Zero, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(3));
        var leases = new List<IProviderAccessLease>();

        for (var index = 0; index < 32; index++)
            leases.Add(await (index % 2 == 0 ? firstReplica : secondReplica)
                .AcquireAsync(request, TestContext.Current.CancellationToken));

        Assert.Equal(16, leases.Count(value => value.Selection!.AccessId == accessA));
        Assert.Equal(16, leases.Count(value => value.Selection!.AccessId == accessB));
        await using var saturated = await firstReplica.AcquireAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(ProviderAccessRejectionReason.Saturated, saturated.RejectionReason);
        foreach (var lease in leases) await lease.DisposeAsync();
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RedisProviderCircuitSharesOpenStateAndSingleHalfOpenProbeAcrossReplicas()
    {
        SkipUnlessEnabled();
        await using var redis = await ConnectionMultiplexer.ConnectAsync(
            Environment.GetEnvironmentVariable("ConnectionStrings__Redis")!);
        var suffix = Guid.NewGuid().ToString("N");
        var selection = new ProviderAccessSelection($"integration-{suffix}", $"access-{suffix}", "provider", "model");
        var options = new ProviderCircuitOptions
        {
            ConsecutiveFailuresToOpen = 2,
            InitialBreakSeconds = 1,
            MaximumBreakSeconds = 1,
            ProbeLeaseSeconds = 5,
        };
        var first = new RedisProviderCircuitRegistry(redis, options);
        var second = new RedisProviderCircuitRegistry(redis, options);
        await first.InitializeAsync([selection], TestContext.Current.CancellationToken);
        await second.InitializeAsync([selection], TestContext.Current.CancellationToken);
        await first.ReportAsync(selection, ProviderCircuitOutcome.TransientFailure, null,
            TestContext.Current.CancellationToken);
        await first.ReportAsync(selection, ProviderCircuitOutcome.TransientFailure, null,
            TestContext.Current.CancellationToken);

        Assert.False(await second.TryAcquireAsync(selection, TestContext.Current.CancellationToken));
        Assert.Equal(ProviderCircuitState.Open,
            Assert.Single(await second.SnapshotAsync(TestContext.Current.CancellationToken)).State);

        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);
        var probes = await Task.WhenAll(
            first.TryAcquireAsync(selection, TestContext.Current.CancellationToken).AsTask(),
            second.TryAcquireAsync(selection, TestContext.Current.CancellationToken).AsTask());
        Assert.Equal(1, probes.Count(value => value));
    }

    private static ReserveOperation Operation(Guid principalId, ServicePolicy policy)
    {
        var resourcePolicy = policy.Get(Resources.QwenFlash);
        return new(Guid.CreateVersion7(), principalId, UsageKind.Chat,
            new(policy.Revision, policy.Fingerprint, Resources.QwenFlash, resourcePolicy.Price, policy.PrincipalDailyAllowance, new(100), new(100)),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32),
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));
    }

    private static async Task<OperationHandle> ReserveDispatchedAsync(
        PostgresOperationLedger ledger,
        ReserveOperation operation,
        CancellationToken token)
    {
        var reservation = await ledger.ReserveAsync(operation, token);
        var handle = Assert.IsType<OperationHandle>(reservation.Handle);
        Assert.Equal(OwnershipMutationResult.Applied,
            await ledger.MarkDispatchedAsync(handle, TimeSpan.FromSeconds(30), token));
        return handle;
    }

    private static async Task<Guid> AddPrincipalAsync(
        DbContextOptions<SnowShotDbContext> options,
        CancellationToken token)
    {
        var principalId = Guid.CreateVersion7();
        await using var context = new SnowShotDbContext(options);
        context.Principals.Add(new PrincipalEntity { Id = principalId, CreatedAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync(token);
        return principalId;
    }

    private static DbContextOptions<SnowShotDbContext> FaultOptions(
        string connectionString,
        CommitAcknowledgementLossInterceptor interceptor) =>
        new DbContextOptionsBuilder<SnowShotDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql
                .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)
                .EnableRetryOnFailure(3))
            .AddInterceptors(interceptor)
            .Options;

    private static void SkipUnlessEnabled()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            Assert.Skip("Set SNOWSHOT_RUN_INTEGRATION=true to run PostgreSQL/Redis integration tests.");
    }

    private static IdentityOptions Identity(byte[] current, byte[]? previous = null) => new()
    {
        HmacKeyBase64 = Convert.ToBase64String(current),
        PreviousHmacKeyBase64 = previous is null ? null : Convert.ToBase64String(previous),
    };

    internal static DbContextOptions<SnowShotDbContext> DatabaseOptions(string connectionString) =>
        new DbContextOptionsBuilder<SnowShotDbContext>().UseNpgsql(connectionString, npgsql => npgsql
            .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)).Options;

    internal sealed class ContextFactory(DbContextOptions<SnowShotDbContext> options) : IDbContextFactory<SnowShotDbContext>
    {
        public SnowShotDbContext CreateDbContext() => new(options);
    }

    private sealed class CommitAcknowledgementLossInterceptor : DbTransactionInterceptor
    {
        private int _armed;

        public void Arm() => Interlocked.Exchange(ref _armed, 1);

        public override Task TransactionCommittedAsync(
            DbTransaction transaction,
            TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1)
                throw new NpgsqlException("Simulated lost commit acknowledgement.", new TimeoutException());
            return Task.CompletedTask;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DistributedIntegrationGroup : ICollectionFixture<DistributedIntegrationDatabaseFixture>
{
    public const string Name = "Distributed integration tests";
}

public sealed class DistributedIntegrationDatabaseFixture : IAsyncLifetime
{
    public async ValueTask InitializeAsync()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase))
            return;

        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot")
            ?? throw new InvalidOperationException("ConnectionStrings__SnowShot is required for integration tests.");
        var options = DistributedIntegrationTests.DatabaseOptions(connectionString);
        await using var migration = new SnowShotDbContext(options);
        await migration.Database.MigrateAsync();
        var factory = new DistributedIntegrationTests.ContextFactory(options);
        var policy = ServicePolicy.Defaults();
        var registry = new PostgresPolicyRegistry(factory, policy,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresPolicyRegistry>.Instance);
        await registry.ActivateAsync(CancellationToken.None);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
