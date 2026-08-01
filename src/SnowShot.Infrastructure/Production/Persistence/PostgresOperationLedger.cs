using System.Data;
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Persistence;

internal sealed class PostgresOperationLedger(
    IDbContextFactory<SnowShotDbContext> contextFactory,
    ServicePolicy policy,
    ILogger<PostgresOperationLedger> logger) : IOperationLedger
{
    public PostgresOperationLedger(IDbContextFactory<SnowShotDbContext> contextFactory, ServicePolicy policy)
        : this(contextFactory, policy, Microsoft.Extensions.Logging.Abstractions.NullLogger<PostgresOperationLedger>.Instance) { }

    private static readonly Action<ILogger, long, string, long?, string?, Exception?> StalePolicyReplica =
        LoggerMessage.Define<long, string, long?, string?>(LogLevel.Error,
            new EventId(5401, nameof(StalePolicyReplica)),
            "Reservation rejected for stale policy replica. Configured revision {ConfiguredRevision} fingerprint {ConfiguredFingerprint}; active revision {ActiveRevision} fingerprint {ActiveFingerprint}");

    public async Task<OperationReservation> ReserveAsync(ReserveOperation operation, CancellationToken cancellationToken)
    {
        Validate(operation);
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await ReserveCoreAsync(context, operation, cancellationToken);
        });
    }

    private async Task<OperationReservation> ReserveCoreAsync(
        SnowShotDbContext context,
        ReserveOperation operation,
        CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("operation.reserve");
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var now = await DatabaseNowAsync(context, cancellationToken);
        // Global reservation lock order: active policy, allowance, daily budget, monthly budget.
        var activePolicy = await PolicyRegistryQueries.ReadActiveAsync(context, lockState: true, cancellationToken);
        var configuredFingerprint = Convert.FromHexString(policy.Fingerprint);
        if (activePolicy is null || activePolicy.Revision != policy.Revision ||
            !activePolicy.Fingerprint.AsSpan().SequenceEqual(configuredFingerprint))
        {
            StalePolicyReplica(logger, policy.Revision, policy.Fingerprint, activePolicy?.Revision,
                activePolicy is null ? null : Convert.ToHexString(activePolicy.Fingerprint).ToLowerInvariant(), null);
            SnowShotTelemetry.StalePolicyReplicas.Add(1, new KeyValuePair<string, object?>[] { new("stage", "reservation") });
            SnowShotTelemetry.PolicyReservationRejections.Add(1, new KeyValuePair<string, object?>[] { new("reason", "stale_replica") });
            await transaction.RollbackAsync(cancellationToken);
            return Rejected(ReservationRejectionReason.PolicyUnavailable);
        }
        var allowanceDate = await DatabaseAllowanceDateAsync(context, cancellationToken);
        var dailyKey = allowanceDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var monthlyKey = allowanceDate.ToString("yyyyMM", CultureInfo.InvariantCulture);
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO snowshot.allowance_periods ("PrincipalId", "PeriodDate", "LimitNanoYuan", "CommittedNanoYuan", "ReservedNanoYuan", "AppliedPolicyRevision", "UpdatedAt")
            VALUES ({operation.PrincipalId}, {allowanceDate}, {policy.PrincipalDailyAllowance.Value}, 0, 0, {policy.Revision}, {now})
            ON CONFLICT ("PrincipalId", "PeriodDate") DO NOTHING
            """, cancellationToken);
        await EnsureBudgetAsync(context, BudgetPeriodKind.Daily, dailyKey, policy.DailyOperatorBudget.Value, now, cancellationToken);
        await EnsureBudgetAsync(context, BudgetPeriodKind.Monthly, monthlyKey, policy.MonthlyOperatorBudget.Value, now, cancellationToken);

        // Accounting locks are always allowance, daily budget, then monthly budget.
        var allowance = await context.AllowancePeriods.FromSqlInterpolated($"""
            SELECT * FROM snowshot.allowance_periods
            WHERE "PrincipalId" = {operation.PrincipalId} AND "PeriodDate" = {allowanceDate}
            FOR UPDATE
            """).SingleAsync(cancellationToken);
        var budgets = await LockBudgetsAsync(context, dailyKey, monthlyKey, cancellationToken);
        allowance.LimitNanoYuan = policy.PrincipalDailyAllowance.Value;
        allowance.AppliedPolicyRevision = policy.Revision;
        allowance.UpdatedAt = now;
        foreach (var budget in budgets)
        {
            budget.LimitNanoYuan = budget.Kind == BudgetPeriodKind.Daily
                ? policy.DailyOperatorBudget.Value
                : policy.MonthlyOperatorBudget.Value;
            budget.AppliedPolicyRevision = policy.Revision;
            budget.UpdatedAt = now;
        }
        // Policy changes are immediate even when this reservation is rejected by the new cap.
        await context.SaveChangesAsync(cancellationToken);
        var existing = await context.UsageOperations.AsNoTracking()
            .SingleOrDefaultAsync(value => value.IdempotencyHash == operation.IdempotencyHash, cancellationToken);
        if (existing is not null)
        {
            if (CanRecoverCommittedReservation(existing, operation, now))
            {
                await transaction.CommitAsync(cancellationToken);
                return new(true, Handle(existing), existing.State);
            }
            SnowShotTelemetry.DuplicateRequests.Add(1);
            await transaction.CommitAsync(cancellationToken);
            return new(false, null, existing.State, ReservationRejectionReason.DuplicateRequest);
        }
        if (ReservationRules.WouldExceed(allowance.CommittedNanoYuan, allowance.ReservedNanoYuan,
            operation.Snapshot.PublicReservation.Value, allowance.LimitNanoYuan))
        {
            await transaction.CommitAsync(cancellationToken);
            return Rejected(ReservationRejectionReason.AllowanceExhausted,
                await DatabaseDailyRetryAfterAsync(context, cancellationToken));
        }
        if (budgets.Any(value => ReservationRules.WouldExceed(value.CommittedNanoYuan, value.ReservedNanoYuan,
            operation.Snapshot.OperatorMaximum.Value, value.LimitNanoYuan)))
        {
            await transaction.CommitAsync(cancellationToken);
            var monthlyExhausted = budgets.Any(value => value.Kind == BudgetPeriodKind.Monthly &&
                ReservationRules.WouldExceed(value.CommittedNanoYuan, value.ReservedNanoYuan,
                    operation.Snapshot.OperatorMaximum.Value, value.LimitNanoYuan));
            return Rejected(ReservationRejectionReason.OperatorBudgetExhausted, monthlyExhausted
                ? await DatabaseMonthlyRetryAfterAsync(context, cancellationToken)
                : await DatabaseDailyRetryAfterAsync(context, cancellationToken));
        }

        allowance.ReservedNanoYuan = checked(allowance.ReservedNanoYuan + operation.Snapshot.PublicReservation.Value);
        allowance.UpdatedAt = now;
        foreach (var budget in budgets)
        {
            budget.ReservedNanoYuan = checked(budget.ReservedNanoYuan + operation.Snapshot.OperatorMaximum.Value);
            budget.UpdatedAt = now;
        }
        var absoluteDeadline = now.Add(operation.ExecutionTimeout);
        var leaseExpiresAt = Minimum(now.Add(operation.LeaseTtl), absoluteDeadline);
        var entity = new UsageOperationEntity
        {
            Id = operation.Id,
            PrincipalId = operation.PrincipalId,
            AllowanceDate = allowanceDate,
            Kind = operation.Kind,
            Resource = operation.Snapshot.Resource,
            IdempotencyHash = operation.IdempotencyHash.ToArray(),
            OwnerToken = operation.OwnerToken.ToArray(),
            Fence = 1,
            PolicyFingerprint = Convert.FromHexString(operation.Snapshot.PolicyFingerprint),
            PolicyRevision = operation.Snapshot.PolicyRevision,
            InputRateNanoYuan = operation.Snapshot.Price.Input.Value,
            OutputRateNanoYuan = operation.Snapshot.Price.Output.Value,
            AllowanceLimitNanoYuan = operation.Snapshot.AllowanceLimit.Value,
            ReservedPublicNanoYuan = operation.Snapshot.PublicReservation.Value,
            ReservedOperatorNanoYuan = operation.Snapshot.OperatorMaximum.Value,
            State = ReservationState.Reserved,
            CreatedAt = now,
            AbsoluteDeadline = absoluteDeadline,
            LeaseExpiresAt = leaseExpiresAt,
        };
        context.UsageOperations.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(true, Handle(entity), entity.State);
    }

    public Task<OwnershipMutationResult> MarkDispatchedAsync(
        OperationHandle handle,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken) =>
        MutateLeaseAsync(handle, leaseTtl, dispatch: true, cancellationToken);

    public Task<OwnershipMutationResult> RenewAsync(
        OperationHandle handle,
        TimeSpan leaseTtl,
        CancellationToken cancellationToken) =>
        MutateLeaseAsync(handle, leaseTtl, dispatch: false, cancellationToken);

    private async Task<OwnershipMutationResult> MutateLeaseAsync(
        OperationHandle handle,
        TimeSpan leaseTtl,
        bool dispatch,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(leaseTtl, TimeSpan.Zero);
        using var activity = SnowShotTelemetry.Activities.StartActivity(dispatch ? "operation.dispatch" : "operation.renew");
        var result = await ExecuteWithStrategyAsync(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var operation = await LockOperationAsync(context, handle.OperationId, skipLocked: false, cancellationToken);
            var now = await DatabaseNowAsync(context, cancellationToken);
            var expectedState = dispatch ? ReservationState.Reserved : ReservationState.Dispatched;
            if (operation is null || operation.State != expectedState || operation.Fence != handle.Fence ||
                !operation.OwnerToken.AsSpan().SequenceEqual(handle.OwnerToken.AsSpan()) ||
                operation.LeaseExpiresAt <= now || operation.AbsoluteDeadline <= now)
            {
                await transaction.CommitAsync(cancellationToken);
                return OwnershipMutationResult.LeaseLost;
            }
            operation.LeaseExpiresAt = Minimum(now.Add(leaseTtl), operation.AbsoluteDeadline);
            if (dispatch)
            {
                operation.State = ReservationState.Dispatched;
                operation.DispatchedAt = now;
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OwnershipMutationResult.Applied;
        }, cancellationToken);
        if (result == OwnershipMutationResult.LeaseLost) SnowShotTelemetry.LostLeases.Add(1);
        else if (!dispatch) SnowShotTelemetry.RenewedLeases.Add(1);
        return result;
    }

    public async Task<OwnershipMutationResult> PrepareAttemptAsync(
        ProviderAttemptPreparation attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.Id == Guid.Empty || attempt.Handle.OperationId == Guid.Empty || attempt.AttemptNumber <= 0 ||
            string.IsNullOrWhiteSpace(attempt.Provider) || string.IsNullOrWhiteSpace(attempt.Resource))
            throw new ArgumentException("Provider attempt preparation is invalid.", nameof(attempt));
        using var activity = SnowShotTelemetry.Activities.StartActivity("provider.attempt.prepare");
        return await ExecuteWithStrategyAsync(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var operation = await LockOperationAsync(context, attempt.Handle.OperationId, skipLocked: false, cancellationToken);
            var now = await DatabaseNowAsync(context, cancellationToken);
            if (!IsOwned(operation, attempt.Handle, ReservationState.Dispatched, now))
            {
                await transaction.CommitAsync(cancellationToken);
                return OwnershipMutationResult.LeaseLost;
            }
            var existing = await context.ProviderAttempts.SingleOrDefaultAsync(value =>
                value.Id == attempt.Id ||
                (value.OperationId == attempt.Handle.OperationId && value.AttemptNumber == attempt.AttemptNumber),
                cancellationToken);
            if (existing is not null)
            {
                if (existing.Id != attempt.Id || existing.OperationId != attempt.Handle.OperationId ||
                    existing.AttemptNumber != attempt.AttemptNumber ||
                    !string.Equals(existing.Provider, attempt.Provider, StringComparison.Ordinal) ||
                    !string.Equals(existing.Resource, attempt.Resource, StringComparison.Ordinal) ||
                    !SameDatabaseTimestamp(existing.StartedAt, attempt.StartedAt) ||
                    existing.State != ProviderAttemptState.Prepared ||
                    existing.DispatchState != AttemptDispatchState.Prepared)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    throw new InvalidOperationException("Provider attempt preparation conflicts with an existing checkpoint.");
                }
                await transaction.CommitAsync(cancellationToken);
                return OwnershipMutationResult.Applied;
            }
            context.ProviderAttempts.Add(new ProviderAttemptEntity
            {
                Id = attempt.Id,
                OperationId = attempt.Handle.OperationId,
                AttemptNumber = attempt.AttemptNumber,
                Provider = attempt.Provider,
                Resource = attempt.Resource,
                State = ProviderAttemptState.Prepared,
                DispatchState = AttemptDispatchState.Prepared,
                StartedAt = attempt.StartedAt,
            });
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return OwnershipMutationResult.Applied;
        }, cancellationToken);
    }

    public async Task<OwnershipMutationResult> CompleteAttemptAsync(
        OperationHandle handle,
        ProviderAttempt attempt,
        CancellationToken cancellationToken)
    {
        if (attempt.OperationId != handle.OperationId)
            throw new InvalidOperationException("Provider attempt does not belong to the owned operation.");
        using var activity = SnowShotTelemetry.Activities.StartActivity("provider.attempt.complete");
        var completion = await ExecuteWithStrategyAsync<AttemptCompletionResult?>(async context =>
        {
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var operation = await LockOperationAsync(context, handle.OperationId, skipLocked: false, cancellationToken);
            var now = await DatabaseNowAsync(context, cancellationToken);
            if (!IsOwned(operation, handle, ReservationState.Dispatched, now))
            {
                await transaction.CommitAsync(cancellationToken);
                return null;
            }
            var result = await CompleteAttemptLockedAsync(context, attempt, cancellationToken);
            if (result == AttemptCompletionResult.Conflict)
            {
                await transaction.RollbackAsync(cancellationToken);
                throw new InvalidOperationException("Provider attempt completion conflicts with its preparation.");
            }
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }, cancellationToken);
        if (completion is null) return OwnershipMutationResult.LeaseLost;
        if (completion == AttemptCompletionResult.Transitioned) ObserveAttempt(attempt);
        return OwnershipMutationResult.Applied;
    }

    public async Task<SettlementResult> CompleteAsync(OperationCompletion completion, CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
            var settlement = completion.Settlement;
            var operation = await LockOperationAsync(context, settlement.Handle.OperationId, skipLocked: false, cancellationToken);
            if (operation is null)
            {
                await transaction.CommitAsync(cancellationToken);
                return new(false, null, SettlementRejectionReason.LeaseLost);
            }
            if (completion.FinalAttempt is not null)
            {
                if (completion.FinalAttempt.OperationId != settlement.Handle.OperationId)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(false, null, SettlementRejectionReason.Conflict);
                }
                var attemptCompletion = await CompleteAttemptLockedAsync(context, completion.FinalAttempt, cancellationToken);
                if (attemptCompletion == AttemptCompletionResult.Conflict)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new(false, null, SettlementRejectionReason.Conflict);
                }
                await context.SaveChangesAsync(cancellationToken);
                var finalResult = await SettleLockedAsync(context, operation, settlement, requireOwner: true, cancellationToken);
                UsageEventEntity? committedEvent = null;
                if (finalResult.Accepted)
                {
                    committedEvent = context.UsageEvents.Local.SingleOrDefault(value => value.OperationId == operation.Id);
                    await transaction.CommitAsync(cancellationToken);
                    ObserveCommittedSettlement(operation, finalResult, committedEvent);
                }
                else await transaction.RollbackAsync(cancellationToken);
                if (finalResult.Accepted && attemptCompletion == AttemptCompletionResult.Transitioned)
                    ObserveAttempt(completion.FinalAttempt);
                return finalResult;
            }
            var result = await SettleLockedAsync(context, operation, settlement, requireOwner: true, cancellationToken);
            if (result.Accepted)
            {
                var committedEvent = context.UsageEvents.Local.SingleOrDefault(value => value.OperationId == operation.Id);
                await transaction.CommitAsync(cancellationToken);
                ObserveCommittedSettlement(operation, result, committedEvent);
            }
            else await transaction.RollbackAsync(cancellationToken);
            return result;
        });
    }

    private static async Task<AttemptCompletionResult> CompleteAttemptLockedAsync(
        SnowShotDbContext context,
        ProviderAttempt attempt,
        CancellationToken cancellationToken)
    {
        var entity = await context.ProviderAttempts.SingleOrDefaultAsync(value => value.Id == attempt.Id, cancellationToken);
        if (entity is null || entity.OperationId != attempt.OperationId || entity.AttemptNumber != attempt.AttemptNumber ||
            !string.Equals(entity.Provider, attempt.Provider, StringComparison.Ordinal) ||
            !string.Equals(entity.Resource, attempt.Resource, StringComparison.Ordinal) ||
            !SameDatabaseTimestamp(entity.StartedAt, attempt.StartedAt))
            return AttemptCompletionResult.Conflict;
        if (entity.State == ProviderAttemptState.Completed)
            return AttemptMatches(entity, attempt) ? AttemptCompletionResult.AlreadyCompleted : AttemptCompletionResult.Conflict;
        if (attempt.DispatchState == AttemptDispatchState.Prepared ||
            attempt.CompletedAt < attempt.StartedAt || string.IsNullOrWhiteSpace(attempt.Outcome) ||
            (attempt.DispatchState == AttemptDispatchState.NotDispatched &&
             (!attempt.CostKnown || attempt.Cost != NanoYuan.Zero)))
            return AttemptCompletionResult.Conflict;
        entity.State = ProviderAttemptState.Completed;
        entity.DispatchState = attempt.DispatchState;
        entity.Outcome = attempt.Outcome;
        entity.HttpStatus = attempt.HttpStatus;
        entity.InputUnits = attempt.InputUnits;
        entity.OutputUnits = attempt.OutputUnits;
        entity.CostNanoYuan = attempt.Cost.Value;
        entity.CostKnown = attempt.CostKnown;
        entity.CompletedAt = attempt.CompletedAt;
        return AttemptCompletionResult.Transitioned;
    }

    private enum AttemptCompletionResult { Conflict, AlreadyCompleted, Transitioned }

    private sealed record ReconciliationCommit(
        UsageOperationEntity Operation,
        SettlementResult Result,
        UsageEventEntity Event,
        bool Reserved);

    private static async Task<SettlementResult> SettleLockedAsync(
        SnowShotDbContext context,
        UsageOperationEntity operation,
        OperationSettlement settlement,
        bool requireOwner,
        CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("operation.settle");
        var snapshot = Snapshot(operation);
        var sourceState = operation.State.IsTerminal()
            ? operation.DispatchedAt is null ? ReservationState.Reserved : ReservationState.Dispatched
            : operation.State;
        var attempts = await context.ProviderAttempts.Where(value => value.OperationId == operation.Id)
            .AsNoTracking().ToListAsync(cancellationToken);
        var attemptsCertain = attempts.All(value => value.State == ProviderAttemptState.Completed && value.CostKnown &&
            value.DispatchState is AttemptDispatchState.NotDispatched or AttemptDispatchState.Dispatched) &&
            (sourceState == ReservationState.Reserved || attempts.Count > 0);
        var operatorCost = attemptsCertain
            ? new NanoYuan(attempts.Aggregate(0L, (total, attempt) => checked(total + attempt.CostNanoYuan)))
            : snapshot.OperatorMaximum;
        var effective = settlement with
        {
            ReportedOperatorCost = operatorCost,
            CostKnown = settlement.CostKnown && attemptsCertain,
            VerifiableOverage = settlement.VerifiableOverage && attemptsCertain,
        };
        var decision = ReservationRules.Settle(sourceState, snapshot,
            effective.ReportedPublicCost, effective.ReportedOperatorCost, effective.Delivered,
            effective.CostKnown, effective.VerifiableOverage, effective.InputUnits,
            effective.OutputUnits, effective.Outcome);
        var fingerprint = Convert.FromHexString(decision.Fingerprint);
        if (operation.State.IsTerminal())
        {
            return operation.SettlementFingerprint is not null && operation.SettlementFingerprint.SequenceEqual(fingerprint)
                ? new(true, StoredDecision(operation))
                : new(false, null, SettlementRejectionReason.Conflict);
        }

        var now = await DatabaseNowAsync(context, cancellationToken);
        if (requireOwner && (operation.Fence != settlement.Handle.Fence ||
            !operation.OwnerToken.AsSpan().SequenceEqual(settlement.Handle.OwnerToken.AsSpan()) ||
            operation.LeaseExpiresAt <= now || operation.AbsoluteDeadline <= now))
            return new(false, null, SettlementRejectionReason.LeaseLost);
        if (!ReservationRules.CanTransition(operation.State, decision.State))
            return new(false, null, SettlementRejectionReason.Conflict);

        var dailyKey = operation.AllowanceDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var monthlyKey = operation.AllowanceDate.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var allowance = await context.AllowancePeriods.FromSqlInterpolated($"""
            SELECT * FROM snowshot.allowance_periods
            WHERE "PrincipalId" = {operation.PrincipalId} AND "PeriodDate" = {operation.AllowanceDate}
            FOR UPDATE
            """).SingleAsync(cancellationToken);
        var budgets = await LockBudgetsAsync(context, dailyKey, monthlyKey, cancellationToken);
        allowance.ReservedNanoYuan = checked(allowance.ReservedNanoYuan - operation.ReservedPublicNanoYuan);
        allowance.CommittedNanoYuan = checked(allowance.CommittedNanoYuan + decision.PublicCost.Value);
        allowance.UpdatedAt = now;
        foreach (var budget in budgets)
        {
            budget.ReservedNanoYuan = checked(budget.ReservedNanoYuan - operation.ReservedOperatorNanoYuan);
            budget.CommittedNanoYuan = checked(budget.CommittedNanoYuan + decision.OperatorCost.Value);
            budget.UpdatedAt = now;
        }
        operation.State = decision.State;
        operation.ActualPublicNanoYuan = decision.PublicCost.Value;
        operation.ActualOperatorNanoYuan = decision.OperatorCost.Value;
        operation.OperatorOverageNanoYuan = decision.OperatorOverage.Value;
        operation.SettlementFingerprint = fingerprint;
        operation.SettledAt = now;
        context.UsageEvents.Add(new UsageEventEntity
        {
            OperationId = operation.Id,
            PrincipalId = operation.PrincipalId,
            Kind = operation.Kind,
            Resource = operation.Resource,
            Outcome = settlement.Outcome,
            InputUnits = settlement.InputUnits,
            OutputUnits = settlement.OutputUnits,
            PublicCostNanoYuan = decision.PublicCost.Value,
            OperatorCostNanoYuan = decision.OperatorCost.Value,
            OperatorOverageNanoYuan = decision.OperatorOverage.Value,
            CostKnown = effective.CostKnown,
            OccurredAt = now,
        });
        await context.SaveChangesAsync(cancellationToken);
        await UpsertAggregateAsync(context, operation, effective, decision, cancellationToken);
        return new(true, decision);
    }

    public async Task<int> ReconcileExpiredAsync(int maxOperations, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOperations, 1);
        var reconciled = 0;
        for (; reconciled < maxOperations; reconciled++)
        {
            Guid? selectedOperationId = null;
            bool? selectedReserved = null;
            var committed = await ExecuteWithStrategyAsync<ReconciliationCommit?>(async context =>
            {
                await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
                var operation = selectedOperationId is null
                    ? await context.UsageOperations.FromSqlInterpolated($"""
                        SELECT * FROM snowshot.usage_operations
                        WHERE "State" IN ({(int)ReservationState.Reserved}, {(int)ReservationState.Dispatched})
                          AND "LeaseExpiresAt" <= clock_timestamp()
                        ORDER BY "LeaseExpiresAt"
                        FOR UPDATE SKIP LOCKED
                        LIMIT 1
                        """).SingleOrDefaultAsync(cancellationToken)
                    : await context.UsageOperations.FromSqlInterpolated($"""
                        SELECT * FROM snowshot.usage_operations
                        WHERE "Id" = {selectedOperationId.Value}
                        FOR UPDATE
                        """).SingleOrDefaultAsync(cancellationToken);
                if (operation is null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    return null;
                }
                selectedOperationId ??= operation.Id;
                selectedReserved ??= operation.State == ReservationState.Reserved;
                var reserved = selectedReserved.Value;
                var handle = Handle(operation);
                var settlement = new OperationSettlement(handle, NanoYuan.Zero, NanoYuan.Zero,
                    false, true, true, 0, 0,
                    reserved ? "expired_before_dispatch" : "expired_unknown_cost");
                var result = await SettleLockedAsync(context, operation, settlement, requireOwner: false, cancellationToken);
                if (!result.Accepted) throw new InvalidOperationException("Locked reconciliation settlement was rejected.");
                var committedEvent = context.UsageEvents.Local.SingleOrDefault(value => value.OperationId == operation.Id)
                    ?? await context.UsageEvents.AsNoTracking().SingleAsync(value => value.OperationId == operation.Id, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(operation, result, committedEvent, reserved);
            }, cancellationToken);
            if (committed is null) break;
            ObserveCommittedSettlement(committed.Operation, committed.Result, committed.Event);
            SnowShotTelemetry.ReconciliationOutcomes.Add(1,
                new KeyValuePair<string, object?>[] { new("outcome", committed.Reserved ? "released" : "unknown_cost") });
        }
        SnowShotTelemetry.Reconciliations.Add(reconciled);
        await ObserveReconciliationBacklogAsync(cancellationToken);
        return reconciled;
    }

    private async Task ObserveReconciliationBacklogAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = await DatabaseNowAsync(context, cancellationToken);
        var query = context.UsageOperations.AsNoTracking().Where(value =>
            (value.State == ReservationState.Reserved || value.State == ReservationState.Dispatched) &&
            value.LeaseExpiresAt <= now);
        var backlog = await query
            .GroupBy(_ => 1)
            .Select(group => new
            {
                Count = group.LongCount(),
                Oldest = group.Min(value => value.LeaseExpiresAt),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (backlog is null) return;
        SnowShotTelemetry.ReconciliationBacklog.Record(backlog.Count);
        SnowShotTelemetry.ReconciliationOldestAgeSeconds.Record(Math.Max(0, (now - backlog.Oldest).TotalSeconds));
    }

    private async Task<TResult> ExecuteWithStrategyAsync<TResult>(
        Func<SnowShotDbContext, Task<TResult>> operation,
        CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await operation(context);
        });
    }

    private static void Validate(ReserveOperation value)
    {
        if (value.Id == Guid.Empty || value.PrincipalId == Guid.Empty || value.IdempotencyHash.Length != 32 ||
            value.OwnerToken.Length != 32 || value.Snapshot.PolicyRevision <= 0 || value.Snapshot.PolicyFingerprint.Length != 64)
            throw new ArgumentException("Operation identity, hashes, owner token, or policy fingerprint is invalid.");
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.ExecutionTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value.LeaseTtl, TimeSpan.Zero);
        if (value.LeaseTtl > value.ExecutionTimeout) throw new ArgumentException("Lease TTL exceeds the execution timeout.");
    }

    private static OperationReservation Rejected(ReservationRejectionReason reason, TimeSpan? retryAfter = null) =>
        new(false, null, ReservationState.Released, reason, retryAfter);

    private static bool CanRecoverCommittedReservation(
        UsageOperationEntity existing,
        ReserveOperation requested,
        DateTimeOffset now) =>
        existing.State == ReservationState.Reserved && existing.Id == requested.Id &&
        existing.PrincipalId == requested.PrincipalId && existing.Kind == requested.Kind &&
        string.Equals(existing.Resource, requested.Snapshot.Resource, StringComparison.Ordinal) &&
        existing.IdempotencyHash.AsSpan().SequenceEqual(requested.IdempotencyHash) &&
        existing.OwnerToken.AsSpan().SequenceEqual(requested.OwnerToken) &&
        existing.PolicyRevision == requested.Snapshot.PolicyRevision &&
        existing.PolicyFingerprint.AsSpan().SequenceEqual(Convert.FromHexString(requested.Snapshot.PolicyFingerprint)) &&
        existing.InputRateNanoYuan == requested.Snapshot.Price.Input.Value &&
        existing.OutputRateNanoYuan == requested.Snapshot.Price.Output.Value &&
        existing.AllowanceLimitNanoYuan == requested.Snapshot.AllowanceLimit.Value &&
        existing.ReservedPublicNanoYuan == requested.Snapshot.PublicReservation.Value &&
        existing.ReservedOperatorNanoYuan == requested.Snapshot.OperatorMaximum.Value &&
        existing.LeaseExpiresAt > now && existing.AbsoluteDeadline > now;

    private static OperationHandle Handle(UsageOperationEntity value) =>
        new(value.Id, value.OwnerToken, value.Fence, value.AbsoluteDeadline, Snapshot(value));

    private static ReservationSnapshot Snapshot(UsageOperationEntity value) => new(
        value.PolicyRevision, Convert.ToHexString(value.PolicyFingerprint).ToLowerInvariant(), value.Resource,
        new(new(value.InputRateNanoYuan), new(value.OutputRateNanoYuan)), new(value.AllowanceLimitNanoYuan),
        new(value.ReservedPublicNanoYuan), new(value.ReservedOperatorNanoYuan));

    private static SettlementDecision StoredDecision(UsageOperationEntity value) => new(
        value.State, new(value.ActualPublicNanoYuan), new(value.ActualOperatorNanoYuan),
        new(value.OperatorOverageNanoYuan), Convert.ToHexString(value.SettlementFingerprint!).ToLowerInvariant());

    private static Task<UsageOperationEntity?> LockOperationAsync(
        SnowShotDbContext context,
        Guid operationId,
        bool skipLocked,
        CancellationToken token) => context.UsageOperations.FromSqlInterpolated($"""
            SELECT * FROM snowshot.usage_operations WHERE "Id" = {operationId}
            FOR UPDATE
            """).SingleOrDefaultAsync(token);

    private static Task<List<OperatorBudgetPeriodEntity>> LockBudgetsAsync(
        SnowShotDbContext context,
        string dailyKey,
        string monthlyKey,
        CancellationToken token) => context.OperatorBudgetPeriods.FromSqlInterpolated($"""
            SELECT * FROM snowshot.operator_budget_periods
            WHERE ("Kind" = {(int)BudgetPeriodKind.Daily} AND "PeriodKey" = {dailyKey})
               OR ("Kind" = {(int)BudgetPeriodKind.Monthly} AND "PeriodKey" = {monthlyKey})
            ORDER BY "Kind", "PeriodKey" FOR UPDATE
            """).ToListAsync(token);

    private Task<int> EnsureBudgetAsync(SnowShotDbContext context, BudgetPeriodKind kind, string key, long limit, DateTimeOffset now, CancellationToken token) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO snowshot.operator_budget_periods ("Kind", "PeriodKey", "LimitNanoYuan", "CommittedNanoYuan", "ReservedNanoYuan", "AppliedPolicyRevision", "UpdatedAt")
            VALUES ({(int)kind}, {key}, {limit}, 0, 0, {policy.Revision}, {now}) ON CONFLICT ("Kind", "PeriodKey") DO NOTHING
            """, token);

    private static Task<DateTimeOffset> DatabaseNowAsync(SnowShotDbContext context, CancellationToken token) =>
        context.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(token);

    private static Task<DateOnly> DatabaseAllowanceDateAsync(SnowShotDbContext context, CancellationToken token) =>
        context.Database.SqlQuery<DateOnly>($"SELECT (clock_timestamp() AT TIME ZONE 'Asia/Shanghai')::date AS \"Value\"").SingleAsync(token);

    private static Task<TimeSpan> DatabaseDailyRetryAfterAsync(SnowShotDbContext context, CancellationToken token) =>
        context.Database.SqlQuery<TimeSpan>($"""
            SELECT (((date_trunc('day', clock_timestamp() AT TIME ZONE 'Asia/Shanghai') + interval '1 day')
                AT TIME ZONE 'Asia/Shanghai') - clock_timestamp()) AS "Value"
            """).SingleAsync(token);

    private static Task<TimeSpan> DatabaseMonthlyRetryAfterAsync(SnowShotDbContext context, CancellationToken token) =>
        context.Database.SqlQuery<TimeSpan>($"""
            SELECT (((date_trunc('month', clock_timestamp() AT TIME ZONE 'Asia/Shanghai') + interval '1 month')
                AT TIME ZONE 'Asia/Shanghai') - clock_timestamp()) AS "Value"
            """).SingleAsync(token);

    private static DateTimeOffset Minimum(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

    private static bool IsOwned(
        UsageOperationEntity? operation,
        OperationHandle handle,
        ReservationState expectedState,
        DateTimeOffset now) => operation is not null && operation.State == expectedState &&
        operation.Fence == handle.Fence && operation.OwnerToken.AsSpan().SequenceEqual(handle.OwnerToken.AsSpan()) &&
        operation.LeaseExpiresAt > now && operation.AbsoluteDeadline > now;

    private static bool AttemptMatches(ProviderAttemptEntity entity, ProviderAttempt attempt) =>
        entity.State == ProviderAttemptState.Completed && entity.DispatchState == attempt.DispatchState &&
        string.Equals(entity.Outcome, attempt.Outcome, StringComparison.Ordinal) && entity.HttpStatus == attempt.HttpStatus &&
        entity.InputUnits == attempt.InputUnits && entity.OutputUnits == attempt.OutputUnits &&
        entity.CostNanoYuan == attempt.Cost.Value && entity.CostKnown == attempt.CostKnown &&
        entity.CompletedAt is not null && SameDatabaseTimestamp(entity.CompletedAt.Value, attempt.CompletedAt);

    private static bool SameDatabaseTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.UtcDateTime.Ticks / 10 == right.UtcDateTime.Ticks / 10;

    private static void ObserveAttempt(ProviderAttempt attempt)
    {
        SnowShotTelemetry.ProviderAttempts.Add(1, new("provider", attempt.Provider), new("outcome", attempt.Outcome));
        SnowShotTelemetry.ProviderLatencyMilliseconds.Record((attempt.CompletedAt - attempt.StartedAt).TotalMilliseconds,
            new("provider", attempt.Provider), new("resource", attempt.Resource));
    }

    private static void ObserveCommittedSettlement(
        UsageOperationEntity operation,
        SettlementResult result,
        UsageEventEntity? committedEvent)
    {
        if (committedEvent is not null)
            Observe(operation.Resource, committedEvent.Outcome, result.Decision!, committedEvent.CostKnown);
    }

    private static Task<int> UpsertAggregateAsync(SnowShotDbContext context, UsageOperationEntity operation,
        OperationSettlement settlement, SettlementDecision decision, CancellationToken token) =>
        context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO snowshot.daily_aggregates ("UsageDate", "Kind", "Resource", "Requests", "UnknownCostRequests", "InputUnits", "OutputUnits", "PublicCostNanoYuan", "OperatorCostNanoYuan", "OperatorOverageNanoYuan", "UpdatedAt")
            VALUES ({operation.AllowanceDate}, {(int)operation.Kind}, {operation.Resource}, 1, {(!settlement.CostKnown ? 1 : 0)}, {settlement.InputUnits}, {settlement.OutputUnits}, {decision.PublicCost.Value}, {decision.OperatorCost.Value}, {decision.OperatorOverage.Value}, {operation.SettledAt!.Value})
            ON CONFLICT ("UsageDate", "Kind", "Resource") DO UPDATE SET
              "Requests" = snowshot.daily_aggregates."Requests" + 1,
              "UnknownCostRequests" = snowshot.daily_aggregates."UnknownCostRequests" + EXCLUDED."UnknownCostRequests",
              "InputUnits" = snowshot.daily_aggregates."InputUnits" + EXCLUDED."InputUnits",
              "OutputUnits" = snowshot.daily_aggregates."OutputUnits" + EXCLUDED."OutputUnits",
              "PublicCostNanoYuan" = snowshot.daily_aggregates."PublicCostNanoYuan" + EXCLUDED."PublicCostNanoYuan",
              "OperatorCostNanoYuan" = snowshot.daily_aggregates."OperatorCostNanoYuan" + EXCLUDED."OperatorCostNanoYuan",
              "OperatorOverageNanoYuan" = snowshot.daily_aggregates."OperatorOverageNanoYuan" + EXCLUDED."OperatorOverageNanoYuan",
              "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, token);

    private static void Observe(string resource, string outcome, SettlementDecision decision, bool costKnown)
    {
        var tags = new KeyValuePair<string, object?>[] { new("resource", resource) };
        SnowShotTelemetry.PublicCost.Add(decision.PublicCost.Value, tags);
        SnowShotTelemetry.OperatorCost.Add(decision.OperatorCost.Value, tags);
        if (!costKnown) SnowShotTelemetry.UnknownCost.Add(1, tags);
        if (decision.OperatorOverage > NanoYuan.Zero) SnowShotTelemetry.Overage.Add(decision.OperatorOverage.Value, tags);
    }
}
