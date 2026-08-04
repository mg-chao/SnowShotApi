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
            return new(false, false, false, false, null, null);
        var schemaCurrent = !(await context.Database.GetPendingMigrationsAsync(cancellationToken)).Any();
        if (!schemaCurrent) return new(true, false, false, false, null, null);
        var active = await PolicyRegistryQueries.ReadActiveAsync(context, lockState: false, cancellationToken);
        var fingerprint = active is null ? null : Convert.ToHexString(active.Fingerprint).ToLowerInvariant();
        var converged = active?.Revision == configuredPolicy.Revision &&
            string.Equals(fingerprint, configuredPolicy.Fingerprint, StringComparison.Ordinal);
        var allowanceDate = await context.Database.SqlQuery<DateOnly>(
            $"SELECT (clock_timestamp() AT TIME ZONE 'Asia/Shanghai')::date AS \"Value\"")
            .SingleAsync(cancellationToken);
        var dailyKey = allowanceDate.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);
        var monthlyKey = allowanceDate.ToString("yyyyMM", System.Globalization.CultureInfo.InvariantCulture);
        var periods = await context.OperatorBudgetPeriods.AsNoTracking()
            .Where(value => value.Kind == BudgetPeriodKind.Daily && value.PeriodKey == dailyKey ||
                value.Kind == BudgetPeriodKind.Monthly && value.PeriodKey == monthlyKey)
            .ToListAsync(cancellationToken);
        var required = configuredPolicy.ResourcePolicies.Max(value => value.OperatorMaximum.Value);
        var daily = periods.SingleOrDefault(value => value.Kind == BudgetPeriodKind.Daily);
        var monthly = periods.SingleOrDefault(value => value.Kind == BudgetPeriodKind.Monthly);
        var headroom = HasHeadroom(daily, configuredPolicy.DailyOperatorBudget.Value, required) &&
            HasHeadroom(monthly, configuredPolicy.MonthlyOperatorBudget.Value, required);
        return new(true, true, converged, headroom, active?.Revision, fingerprint);
    }

    private static bool HasHeadroom(OperatorBudgetPeriodEntity? period, long configuredLimit, long required) =>
        period is null || !ReservationRules.WouldExceed(period.CommittedNanoYuan, period.ReservedNanoYuan,
            required, configuredLimit);
}
