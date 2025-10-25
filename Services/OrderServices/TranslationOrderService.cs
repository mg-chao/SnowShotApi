using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.OrderServices;

public interface ITranslationOrderService
{
    /// <summary>
    /// 创建翻译订单
    /// </summary>
    /// <param name="type">翻译类型</param>
    /// <param name="content">需要翻译的内容</param>
    /// <returns>翻译订单</returns>
    Task<UserTranslationOrder> CreateAsync(long userId, UserTranslationType type, List<string> content, string from, string to, string domain, int promptTokens, int completionTokens);

    /// <summary>
    /// 更新翻译订单的 From To
    /// </summary>
    /// <param name="orderId">翻译订单ID</param>
    /// <param name="from">源语言</param>
    /// <param name="to">目标语言</param>
    /// <param name="status">翻译订单状态</param>
    /// <param name="promptTokens">提示词数量</param>
    /// <param name="completionTokens">完成词数量</param>
    /// <returns>翻译订单</returns>
    Task<UserTranslationOrder?> UpdateAsync(long orderId, string? from = null, string? to = null, UserTranslationOrderStatus? status = null);
}

public class TranslationOrderService(ApplicationDbContext context, ITranslationOrderStatsService translationOrderStatsService) : ITranslationOrderService
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly ITranslationOrderStatsService _translationOrderStatsService = translationOrderStatsService;

    public async Task<UserTranslationOrder> CreateAsync(long userId, UserTranslationType type, List<string> content, string from, string to, string domain, int promptTokens, int completionTokens)
    {
        var contentLength = content.Sum(c => c.Length);
        var userTranslationOrder = new UserTranslationOrder
        {
            UserId = userId,
            Type = type,
            ContentLength = contentLength,
            From = from,
            To = to,
            Domain = domain,
            Status = UserTranslationOrderStatus.Created,
        };
        await _context.UserTranslationOrders.AddAsync(userTranslationOrder);
        await _context.SaveChangesAsync();

        await _translationOrderStatsService.UpdateAsync(userId, type, contentLength);

        return userTranslationOrder;
    }

    public async Task<UserTranslationOrder?> UpdateAsync(long orderId, string? from = null, string? to = null, UserTranslationOrderStatus? status = null)
    {
        var userTranslationOrder = await _context.UserTranslationOrders.FindAsync(orderId);
        if (userTranslationOrder == null)
        {
            return null;
        }

        if (from != null)
        {
            userTranslationOrder.From = from;
        }
        if (to != null)
        {
            userTranslationOrder.To = to;
        }
        if (status != null)
        {
            userTranslationOrder.Status = status.Value;
        }

        await _context.SaveChangesAsync();

        return userTranslationOrder;
    }
}
