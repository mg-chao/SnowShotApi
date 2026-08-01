using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using Microsoft.EntityFrameworkCore;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Persistence;

namespace SnowShot.Infrastructure.Operations;

public sealed class ReconciliationService(
    IOperationReconciler reconciler,
    MaintenanceOptions options,
    ILogger<ReconciliationService> logger) : BackgroundService
{
    private static readonly Action<ILogger, Exception?> ReconciliationFailed =
        LoggerMessage.Define(LogLevel.Error, new EventId(5101, nameof(ReconciliationFailed)), "Usage reconciliation failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reconciled = await reconciler.ReconcileExpiredAsync(options.ReconciliationBatchSize, stoppingToken);
                var delay = reconciled == options.ReconciliationBatchSize
                    ? TimeSpan.FromMilliseconds(options.BusyDelayMilliseconds)
                    : TimeSpan.FromSeconds(options.ReconciliationIdleSeconds);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            catch (Exception exception)
            {
                ReconciliationFailed(logger, exception);
                await Task.Delay(TimeSpan.FromSeconds(options.FailureDelaySeconds), stoppingToken);
            }
        }
    }
}

public sealed class ReadinessService(
    IPersistenceReadinessProbe persistenceProbe,
    IAdmissionController admission,
    IProviderAccessPool providerAccess,
    IDependencyHealth dependencyHealth,
    SnowShot.Domain.ServicePolicy policy,
    MaintenanceOptions maintenance,
    TimeProvider timeProvider) : IReadinessService
{
    public async Task<ReadinessReport> CheckAsync(CancellationToken cancellationToken)
    {
        var persistence = await CheckAsync(persistenceProbe.CheckReadyAsync, cancellationToken);
        var admissionReady = await CheckAsync(admission.CheckReadyAsync, cancellationToken);
        var providerAccessReady = await CheckAsync(providerAccess.CheckReadyAsync, cancellationToken);
        var components = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["policy"] = persistence?.PolicyConverged ?? false,
            ["schema"] = persistence?.SchemaCurrent ?? false,
            ["postgresql"] = persistence?.Connected ?? false,
            ["admission"] = admissionReady,
            ["provider_access"] = providerAccessReady,
        };
        var staleBefore = timeProvider.GetUtcNow().AddSeconds(-maintenance.DependencyStatusStaleSeconds);
        foreach (var component in dependencyHealth.Snapshot())
            components[component.Key] = component.Value.Healthy && component.Value.ObservedAt >= staleBefore;
        return new(persistence is { Connected: true, SchemaCurrent: true, PolicyConverged: true } && admissionReady && providerAccessReady,
            policy.Revision, policy.Fingerprint, persistence?.ActivePolicyRevision,
            persistence?.ActivePolicyFingerprint, components);
    }

    private static async Task<bool> CheckAsync(Func<CancellationToken, Task<bool>> check, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await check(linked.Token); } catch { return false; }
    }

    private static async Task<PersistenceReadiness?> CheckAsync(
        Func<CancellationToken, Task<PersistenceReadiness>> check,
        CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { return await check(linked.Token); } catch { return null; }
    }
}

public sealed class RetentionService(
    IDbContextFactory<SnowShotDbContext> contextFactory,
    RetentionOptions options,
    MaintenanceOptions maintenance,
    ILogger<RetentionService> logger) : BackgroundService
{
    private static readonly Action<ILogger, int, int, int, int, int, int, Exception?> RetentionCompleted =
        LoggerMessage.Define<int, int, int, int, int, int>(LogLevel.Information,
            new EventId(5201, nameof(RetentionCompleted)),
            "Retention removed {Operations} operations, {Aggregates} aggregates, {AllowancePeriods} allowance periods, " +
            "{BudgetPeriods} budget periods, {Fingerprints} identity fingerprints, and {Principals} principals");
    private static readonly Action<ILogger, Exception?> RetentionFailed = LoggerMessage.Define(
        LogLevel.Error, new EventId(5202, nameof(RetentionFailed)), "Retention failed");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(maintenance.RetentionIntervalHours));
        do
        {
            try
            {
                var removed = RetentionSweepResult.Empty;
                RetentionSweepResult batch;
                do
                {
                    batch = await ApplyAsync(stoppingToken);
                    removed += batch;
                    if (batch.HasFullCategory(maintenance.RetentionBatchSize))
                        await Task.Delay(TimeSpan.FromMilliseconds(maintenance.BusyDelayMilliseconds), stoppingToken);
                }
                while (batch.HasFullCategory(maintenance.RetentionBatchSize));
                RetentionCompleted(logger, removed.Operations, removed.Aggregates, removed.AllowancePeriods,
                    removed.BudgetPeriods, removed.Fingerprints, removed.Principals, null);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                RetentionFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private async Task<RetentionSweepResult> ApplyAsync(CancellationToken cancellationToken)
    {
        await using var strategyContext = await contextFactory.CreateDbContextAsync(cancellationToken);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            return await ApplyCoreAsync(context, options, maintenance.RetentionBatchSize, cancellationToken);
        });
    }

    internal static async Task<RetentionSweepResult> ApplyCoreAsync(
        SnowShotDbContext context,
        RetentionOptions options,
        int batchSize,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var ownsMaintenance = await context.Database.SqlQuery<bool>(
            $"SELECT pg_try_advisory_xact_lock(764219683101) AS \"Value\"").SingleAsync(cancellationToken);
        if (!ownsMaintenance)
        {
            await transaction.RollbackAsync(cancellationToken);
            return RetentionSweepResult.Empty;
        }
        var databaseNow = await context.Database.SqlQuery<DateTimeOffset>(
            $"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
        var operationCutoff = databaseNow.AddDays(-options.OperationDays);
        var aggregateCutoff = DateOnly.FromDateTime(databaseNow.AddDays(-options.AggregateDays).UtcDateTime);
        var identityCutoff = databaseNow.AddDays(-options.IdentityDays);
        var operations = await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "Id" FROM snowshot.usage_operations
                WHERE "SettledAt" < {operationCutoff} AND "State" IN ({(int)ReservationState.Committed}, {(int)ReservationState.Released}, {(int)ReservationState.UnknownCost})
                ORDER BY "SettledAt" LIMIT {batchSize}
            ), attempts AS (
                DELETE FROM snowshot.provider_attempts WHERE "OperationId" IN (SELECT "Id" FROM expired)
            ), events AS (
                DELETE FROM snowshot.usage_events WHERE "OperationId" IN (SELECT "Id" FROM expired)
            )
            DELETE FROM snowshot.usage_operations WHERE "Id" IN (SELECT "Id" FROM expired)
            """, cancellationToken);
        var aggregates = await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "UsageDate", "Kind", "Resource" FROM snowshot.daily_aggregates
                WHERE "UsageDate" < {aggregateCutoff}
                ORDER BY "UsageDate", "Kind", "Resource" LIMIT {batchSize}
            )
            DELETE FROM snowshot.daily_aggregates AS target USING expired
            WHERE target."UsageDate" = expired."UsageDate"
              AND target."Kind" = expired."Kind"
              AND target."Resource" = expired."Resource"
            """, cancellationToken);
        var allowancePeriods = await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "PrincipalId", "PeriodDate" FROM snowshot.allowance_periods
                WHERE "PeriodDate" < {aggregateCutoff}
                ORDER BY "PeriodDate", "PrincipalId" LIMIT {batchSize}
            )
            DELETE FROM snowshot.allowance_periods AS target USING expired
            WHERE target."PrincipalId" = expired."PrincipalId"
              AND target."PeriodDate" = expired."PeriodDate"
            """, cancellationToken);
        var budgetPeriods = await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "Kind", "PeriodKey" FROM snowshot.operator_budget_periods
                WHERE "UpdatedAt" < {identityCutoff} AND "ReservedNanoYuan" = 0
                ORDER BY "UpdatedAt", "Kind", "PeriodKey" LIMIT {batchSize}
            )
            DELETE FROM snowshot.operator_budget_periods AS target USING expired
            WHERE target."Kind" = expired."Kind" AND target."PeriodKey" = expired."PeriodKey"
            """, cancellationToken);
        var fingerprints = await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "Fingerprint" FROM snowshot.principal_fingerprints AS fingerprint
                WHERE fingerprint."LastSeenAt" < {identityCutoff}
                  AND NOT EXISTS (
                    SELECT 1 FROM snowshot.usage_operations AS operation
                    WHERE operation."PrincipalId" = fingerprint."PrincipalId")
                  AND NOT EXISTS (
                    SELECT 1 FROM snowshot.allowance_periods AS allowance
                    WHERE allowance."PrincipalId" = fingerprint."PrincipalId")
                ORDER BY fingerprint."LastSeenAt", fingerprint."Fingerprint" LIMIT {batchSize}
            )
            DELETE FROM snowshot.principal_fingerprints AS target USING expired
            WHERE target."Fingerprint" = expired."Fingerprint"
            """, cancellationToken);
        var principals = await context.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT "Id" FROM snowshot.principals AS principal
                WHERE principal."CreatedAt" < {identityCutoff}
                  AND NOT EXISTS (
                    SELECT 1 FROM snowshot.principal_fingerprints AS fingerprint
                    WHERE fingerprint."PrincipalId" = principal."Id")
                  AND NOT EXISTS (
                    SELECT 1 FROM snowshot.usage_operations AS operation
                    WHERE operation."PrincipalId" = principal."Id")
                  AND NOT EXISTS (
                    SELECT 1 FROM snowshot.allowance_periods AS allowance
                    WHERE allowance."PrincipalId" = principal."Id")
                ORDER BY principal."CreatedAt", principal."Id" LIMIT {batchSize}
            )
            DELETE FROM snowshot.principals AS target USING expired
            WHERE target."Id" = expired."Id"
            """, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(operations, aggregates, allowancePeriods, budgetPeriods, fingerprints, principals);
    }
}

internal readonly record struct RetentionSweepResult(
    int Operations,
    int Aggregates,
    int AllowancePeriods,
    int BudgetPeriods,
    int Fingerprints,
    int Principals)
{
    public static RetentionSweepResult Empty => default;

    public bool HasFullCategory(int batchSize) =>
        Operations == batchSize || Aggregates == batchSize || AllowancePeriods == batchSize ||
        BudgetPeriods == batchSize || Fingerprints == batchSize || Principals == batchSize;

    public static RetentionSweepResult operator +(RetentionSweepResult left, RetentionSweepResult right) => new(
        checked(left.Operations + right.Operations),
        checked(left.Aggregates + right.Aggregates),
        checked(left.AllowancePeriods + right.AllowancePeriods),
        checked(left.BudgetPeriods + right.BudgetPeriods),
        checked(left.Fingerprints + right.Fingerprints),
        checked(left.Principals + right.Principals));
}
