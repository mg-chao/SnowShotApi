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
        var currentDate = GetCurrentDate();
        var promptTokensToAdd = Math.Max(promptTokens, 0);
        var completionTokensToAdd = Math.Max(completionTokens, 0);
        var updatedAt = DateTime.UtcNow;

        // The database constraint is also the UPSERT target, so concurrent first requests
        // for the same user, model, and day produce one row without losing token updates.
        await _context.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "UserChatOrderStats" (
                "UserId",
                "Date",
                "Model",
                "PromptTokensSum",
                "CompletionTokensSum",
                "UpdatedAt")
            VALUES (
                {userId},
                {currentDate},
                {model},
                {promptTokensToAdd},
                {completionTokensToAdd},
                {updatedAt})
            ON CONFLICT ("UserId", "Date", "Model")
            DO UPDATE SET
                "PromptTokensSum" = GREATEST(
                    "UserChatOrderStats"."PromptTokensSum" + EXCLUDED."PromptTokensSum",
                    0),
                "CompletionTokensSum" = GREATEST(
                    "UserChatOrderStats"."CompletionTokensSum" + EXCLUDED."CompletionTokensSum",
                    0),
                "UpdatedAt" = EXCLUDED."UpdatedAt";
            """);

        return await GetAsync(userId, model, currentDate);
    }

    public async Task<UserChatOrderStats?> GetAsync(long userId, string model)
    {
        return await GetAsync(userId, model, GetCurrentDate());
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

    private Task<UserChatOrderStats?> GetAsync(long userId, string model, int currentDate)
    {
        return _context.UserChatOrderStats
            .AsNoTracking()
            .FirstOrDefaultAsync(stats =>
                stats.UserId == userId &&
                stats.Model == model &&
                stats.Date == currentDate);
    }

    private static int GetCurrentDate() => int.Parse(DateTime.Now.ToString("yyyyMMdd"));
}
