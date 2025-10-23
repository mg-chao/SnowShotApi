using Microsoft.EntityFrameworkCore;
using SnowShotApi.AppEnvs;
using SnowShotApi.Data;
using SnowShotApi.Models;
using SnowShotApi.RequestValidations;

namespace SnowShotApi.Services.OrderServices;

public interface IChatOrderStatsService
{
    /// <summary>
    /// 更新聊天订单统计
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="model">模型</param>
    /// <param name="promptTokens">提示词 tokens</param>
    /// <param name="completionTokens">完成 tokens</param>
    /// <returns>聊天订单统计</returns>
    Task<UserChatOrderStats?> UpdateAsync(long userId, string model, int promptTokens, int completionTokens);

    /// <summary>
    /// 获取用户订单统计
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="model">模型</param>
    /// <returns>用户订单统计</returns>
    Task<UserChatOrderStats?> GetAsync(long userId, string model);


    /// <summary>
    /// 判断用户是否达到限额
    /// </summary>
    /// <param name="userId">用户 ID</param>
    /// <param name="model">模型</param>
    /// <returns>是否达到限额</returns>
    Task<bool> IsLimitIpUserAsync(long userId, string model);
}

public class ChatOrderStatsService(ApplicationDbContext context) : IChatOrderStatsService
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly ChatApiEnv _chatApiEnv = new();

    public async Task<UserChatOrderStats?> UpdateAsync(long userId, string model, int promptTokens, int completionTokens)
    {
        // 获取当前日期
        var currentDateStr = DateTime.Now.Date.ToString("yyyyMMdd");
        if (string.IsNullOrEmpty(currentDateStr))
        {
            return null;
        }
        var currentDate = int.Parse(currentDateStr);

        var stats = await _context.UserChatOrderStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Model == model && s.Date == currentDate);
        if (stats == null)
        {
            stats = new UserChatOrderStats
            {
                UserId = userId,
                Date = currentDate,
                Model = model,
                PromptTokensSum = 0,
                CompletionTokensSum = 0,
            };
            await _context.UserChatOrderStats.AddAsync(stats);
        }

        stats.PromptTokensSum = Math.Max(stats.PromptTokensSum + promptTokens, 0);
        stats.CompletionTokensSum = Math.Max(stats.CompletionTokensSum + completionTokens, 0);
        stats.UpdatedAt = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return stats;
    }

    public async Task<UserChatOrderStats?> GetAsync(long userId, string model)
    {
        return await _context.UserChatOrderStats.FirstOrDefaultAsync(s => s.UserId == userId && s.Model == model);
    }

    public async Task<bool> IsLimitIpUserAsync(long userId, string model)
    {
        var modelInfo = ChatModelAttribute.ValidModels[model];
        if (modelInfo == null)
        {
            return true;
        }

        var stats = await GetAsync(userId, model);
        if (stats == null)
        {
            return false;
        }

        return (stats.PromptTokensSum / 1000M * modelInfo.PromptTokenPrice + stats.CompletionTokensSum / 1000M * modelInfo.CompletionTokenPrice) >= _chatApiEnv.UserCostLimit;
    }
}
