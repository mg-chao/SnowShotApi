using System.Text;
using Microsoft.EntityFrameworkCore;
using SnowShotApi.AppEnvs;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.OrderServices;

public interface ITranslationOrderStatsService
{
    /// <summary>
    /// 更新翻译订单统计
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="type">订单类型</param>
    /// <param name="contentLength">内容长度</param>
    /// <returns>翻译订单统计</returns>
    Task<UserTranslationOrderStats?> UpdateAsync(long userId, UserTranslationType type, int contentLength);

    /// <summary>
    /// 获取用户订单统计
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="type">订单类型</param>
    /// <returns>用户订单统计</returns>
    Task<UserTranslationOrderStats?> GetAsync(long userId, UserTranslationType type);

    /// <summary>
    /// 判断用户是否达到限额
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="type">订单类型</param>
    /// <returns>是否达到限额</returns>
    Task<bool> IsLimitIpUserAsync(long userId, UserTranslationType type);
}

public class TranslationOrderStatsService(ApplicationDbContext context) : ITranslationOrderStatsService
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly TranslationApiEnv _translationApiEnv = new();

    public async Task<UserTranslationOrderStats?> UpdateAsync(long userId, UserTranslationType type, int contentLength)
    {
        // 获取当前日期
        var currentDateStr = DateTime.Now.Date.ToString("yyyyMMdd");
        if (string.IsNullOrEmpty(currentDateStr))
        {
            return null;
        }
        var currentDate = int.Parse(currentDateStr);

        // 获取当前日期对应的翻译订单统计
        var stats = await _context.UserTranslationOrderStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Type == type && s.Date == currentDate);
        if (stats == null)
        {
            stats = new UserTranslationOrderStats
            {
                UserId = userId,
                Date = currentDate,
                Type = type,
                ContentLengthSum = 0,
            };
            await _context.UserTranslationOrderStats.AddAsync(stats);
        }

        stats.ContentLengthSum = Math.Max(stats.ContentLengthSum + contentLength, 0);
        stats.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return stats;
    }

    public async Task<UserTranslationOrderStats?> GetAsync(long userId, UserTranslationType type)
    {
        return await _context.UserTranslationOrderStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Type == type);
    }

    public async Task<bool> IsLimitIpUserAsync(long userId, UserTranslationType type)
    {
        var stats = await GetAsync(userId, type);
        return stats != null && stats.ContentLengthSum >= _translationApiEnv.ContentLengthLimit;
    }
}
