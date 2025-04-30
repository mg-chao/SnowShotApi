using System.Text;
using Microsoft.EntityFrameworkCore;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.TranslationServices;

public interface ITranslationOrderStatsService
{
    /// <summary>
    /// 更新翻译订单统计
    /// </summary>
    /// <param name="order">用户订单</param>
    /// <param name="translationOrder">翻译订单</param>
    /// <returns>翻译订单统计</returns>
    Task<UserTranslationUserOrderStats?> UpdateTranslationOrderStatsAsync(UserOrder order, UserTranslationOrder translationOrder);

    /// <summary>
    /// 获取用户订单统计
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="type">订单类型</param>
    /// <returns>用户订单统计</returns>
    Task<UserTranslationUserOrderStats?> GetUserTranslationUserOrderStatsAsync(long userId, UserTranslationType type);
}

public class TranslationOrderStatsService(ApplicationDbContext context) : ITranslationOrderStatsService
{
    protected readonly ApplicationDbContext _context = context;

    public async Task<UserTranslationUserOrderStats?> UpdateTranslationOrderStatsAsync(UserOrder order, UserTranslationOrder translationOrder)
    {
        // 获取当前日期
        var currentDateStr = DateTime.Now.Date.ToString("yyyyMMdd");
        if (string.IsNullOrEmpty(currentDateStr))
        {
            return null;
        }
        var currentDate = int.Parse(currentDateStr);

        // 获取当前日期对应的翻译订单统计
        var stats = await _context.UserTranslationUserOrderStats.FirstOrDefaultAsync(s => s.UserId == order.UserId && s.Type == translationOrder.Type && s.Date == currentDate);
        if (stats == null)
        {
            stats = new UserTranslationUserOrderStats
            {
                UserId = order.UserId,
                Date = currentDate,
                Type = translationOrder.Type,
                ContentLengthSum = 0,
                ContentByteCountSum = 0,
            };
            await _context.UserTranslationUserOrderStats.AddAsync(stats);
        }

        stats.ContentLengthSum += translationOrder.ContentLength;
        stats.ContentByteCountSum += translationOrder.ContentByteCount;
        stats.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return stats;
    }

    public async Task<UserTranslationUserOrderStats?> GetUserTranslationUserOrderStatsAsync(long userId, UserTranslationType type)
    {
        return await _context.UserTranslationUserOrderStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Type == type);
    }
}
