using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SnowShot.Domain;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Persistence;

internal sealed record ActivePolicy(long Revision, byte[] Fingerprint);

internal static class PolicyRegistryQueries
{
    public static Task<PolicyStateEntity> LockStateAsync(SnowShotDbContext context, CancellationToken token) =>
        context.PolicyStates.FromSqlRaw("""
            SELECT * FROM snowshot.policy_state WHERE "Id" = 1 FOR UPDATE
            """).SingleAsync(token);

    public static async Task<ActivePolicy?> ReadActiveAsync(SnowShotDbContext context, bool lockState, CancellationToken token)
    {
        var state = lockState
            ? await LockStateAsync(context, token)
            : await context.PolicyStates.AsNoTracking().SingleOrDefaultAsync(value => value.Id == 1, token);
        if (state?.ActiveRevision is null) return null;
        var revision = await context.PolicyRevisions.AsNoTracking()
            .SingleAsync(value => value.Revision == state.ActiveRevision.Value, token);
        return new(revision.Revision, revision.Fingerprint);
    }
}

internal sealed class PostgresPolicyRegistry(
    IDbContextFactory<SnowShotDbContext> factory,
    ServicePolicy configuredPolicy,
    ILogger<PostgresPolicyRegistry> logger)
{
    private static readonly Action<ILogger, long, string, Exception?> ActivationIdempotent =
        LoggerMessage.Define<long, string>(LogLevel.Information, new EventId(5402, nameof(ActivationIdempotent)),
            "Policy activation is idempotent for revision {PolicyRevision} fingerprint {PolicyFingerprint}");
    private static readonly Action<ILogger, long, string, Exception?> PolicyActivated =
        LoggerMessage.Define<long, string>(LogLevel.Information, new EventId(5403, nameof(PolicyActivated)),
            "Activated policy revision {PolicyRevision} fingerprint {PolicyFingerprint}");

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        await using var context = await factory.CreateDbContextAsync(cancellationToken);
        var strategy = context.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var retryContext = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await retryContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var databaseNow = await retryContext.Database
                .SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
            await retryContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO snowshot.policy_state ("Id", "ActiveRevision", "UpdatedAt")
                VALUES ({(short)1}, NULL, {databaseNow}) ON CONFLICT ("Id") DO NOTHING
                """, cancellationToken);
            var state = await PolicyRegistryQueries.LockStateAsync(retryContext, cancellationToken);
            PolicyRevisionEntity? active = null;
            if (state.ActiveRevision is not null)
            {
                active = await retryContext.PolicyRevisions.SingleAsync(
                    value => value.Revision == state.ActiveRevision.Value, cancellationToken);
                var activeFingerprint = Convert.ToHexString(active.Fingerprint).ToLowerInvariant();
                var decision = PolicyActivationRules.Decide(active.Revision, activeFingerprint,
                    configuredPolicy.Revision, configuredPolicy.Fingerprint);
                if (decision == PolicyActivationDecision.LowerRevision)
                {
                    SnowShotTelemetry.PolicyActivation.Add(1, new KeyValuePair<string, object?>[] { new("outcome", "lower_revision") });
                    throw new PolicyActivationException($"Configured policy revision {configuredPolicy.Revision} is lower than active revision {active.Revision}.");
                }
                if (decision == PolicyActivationDecision.RevisionConflict)
                {
                    SnowShotTelemetry.PolicyActivation.Add(1, new KeyValuePair<string, object?>[] { new("outcome", "revision_conflict") });
                    throw new PolicyActivationException($"Policy revision {configuredPolicy.Revision} is already registered with different content.");
                }
                if (decision == PolicyActivationDecision.Idempotent)
                {
                    await transaction.CommitAsync(cancellationToken);
                    ActivationIdempotent(logger, configuredPolicy.Revision, configuredPolicy.Fingerprint, null);
                    SnowShotTelemetry.PolicyActivation.Add(1, new KeyValuePair<string, object?>[] { new("outcome", "idempotent") });
                    return;
                }
            }

            var sameRevision = await retryContext.PolicyRevisions.SingleOrDefaultAsync(
                value => value.Revision == configuredPolicy.Revision, cancellationToken);
            if (sameRevision is not null)
            {
                throw new PolicyActivationException($"Policy revision {configuredPolicy.Revision} exists but is not the active revision.");
            }
            retryContext.PolicyRevisions.Add(new PolicyRevisionEntity
            {
                Revision = configuredPolicy.Revision,
                Fingerprint = Convert.FromHexString(configuredPolicy.Fingerprint),
                CanonicalDocument = configuredPolicy.CanonicalDocument,
                PrincipalDailyAllowanceNanoYuan = configuredPolicy.PrincipalDailyAllowance.Value,
                DailyOperatorBudgetNanoYuan = configuredPolicy.DailyOperatorBudget.Value,
                MonthlyOperatorBudgetNanoYuan = configuredPolicy.MonthlyOperatorBudget.Value,
                ActivatedAt = databaseNow,
            });
            state.ActiveRevision = configuredPolicy.Revision;
            state.UpdatedAt = databaseNow;
            await retryContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            PolicyActivated(logger, configuredPolicy.Revision, configuredPolicy.Fingerprint, null);
            SnowShotTelemetry.PolicyActivation.Add(1, new KeyValuePair<string, object?>[] { new("outcome", active is null ? "initial" : "advanced") });
        });
    }
}

internal sealed class PolicyActivationHostedService(PostgresPolicyRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => registry.ActivateAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class PolicyActivationException(string message) : Exception(message);
