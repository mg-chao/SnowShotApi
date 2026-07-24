using Microsoft.EntityFrameworkCore;
using SnowShotApi.Data;
using SnowShotApi.Models;
using SnowShotApi.Services.TranslationServices.AI;

namespace SnowShotApi.Services.OrderServices;

public interface ITranslationOrderStatsService
{
    Task<UserTranslationOrderStats?> GetAsync(
        long userId,
        UserTranslationType type,
        CancellationToken cancellationToken = default);

    Task<bool> IsLimitIpUserAsync(
        long userId,
        UserTranslationType type,
        CancellationToken cancellationToken = default);
}

public sealed class TranslationOrderStatsService(
    ApplicationDbContext context,
    AITranslationOptions options,
    TranslationQuotaClock clock) : ITranslationOrderStatsService
{
    public Task<UserTranslationOrderStats?> GetAsync(
        long userId,
        UserTranslationType type,
        CancellationToken cancellationToken = default)
    {
        var quotaDate = clock.GetCurrentQuotaDate();
        return context.UserTranslationOrderStats
            .AsNoTracking()
            .FirstOrDefaultAsync(
                stats => stats.UserId == userId && stats.Type == type && stats.Date == quotaDate,
                cancellationToken);
    }

    public async Task<bool> IsLimitIpUserAsync(
        long userId,
        UserTranslationType type,
        CancellationToken cancellationToken = default)
    {
        var stats = await GetAsync(userId, type, cancellationToken);
        return stats != null && stats.ContentLengthSum >= options.ContentLengthLimit;
    }
}
