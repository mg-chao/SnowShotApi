using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShot.Infrastructure.Persistence;

internal sealed class PostgresPersistenceReadinessProbe(
    IDbContextFactory<SnowShotDbContext> contextFactory,
    ServicePolicy configuredPolicy) : IPersistenceReadinessProbe
{
    public async Task<PersistenceReadiness> CheckReadyAsync(CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (!await context.Database.CanConnectAsync(cancellationToken))
            return new(false, false, false, null, null);
        var schemaCurrent = !(await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
        if (!schemaCurrent) return new(true, false, false, null, null);
        var active = await PolicyRegistryQueries.ReadActiveAsync(context, lockState: false, cancellationToken);
        var fingerprint = active is null ? null : Convert.ToHexString(active.Fingerprint).ToLowerInvariant();
        var converged = active?.Revision == configuredPolicy.Revision &&
            string.Equals(fingerprint, configuredPolicy.Fingerprint, StringComparison.Ordinal);
        return new(true, true, converged, active?.Revision, fingerprint);
    }
}
