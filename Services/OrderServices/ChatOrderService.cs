using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.OrderServices;

public interface IChatOrderService
{
    /// <summary>
    /// 创建聊天订单
    /// </summary>
    /// <param name="type">聊天类型</param>
    /// <returns>聊天订单</returns>
    Task<UserChatOrder> CreateChatOrderAsync(long userId, string model);

    /// <summary>
    /// 更新聊天订单
    /// </summary>
    /// <param name="orderId">聊天订单ID</param>
    /// <param name="promptTokens">提示词 tokens</param>
    /// <param name="completionTokens">完成 tokens</param>
    /// <param name="status">聊天订单状态</param>
    /// <returns>聊天订单</returns>
    Task<UserChatOrder?> UpdateChatOrderAsync(long orderId, int? promptTokens = null, int? completionTokens = null, UserChatOrderStatus? status = null);
}

public class ChatOrderService(ApplicationDbContext context, IChatOrderStatsService chatOrderStatsService) : IChatOrderService
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly IChatOrderStatsService _chatOrderStatsService = chatOrderStatsService;

    public async Task<UserChatOrder> CreateChatOrderAsync(long userId, string model)
    {
        var userChatOrder = new UserChatOrder
        {
            UserId = userId,
            Model = model,
            Status = UserChatOrderStatus.Created,
            PromptTokens = 0,
            CompletionTokens = 0,
        };
        await _context.UserChatOrders.AddAsync(userChatOrder);
        await _context.SaveChangesAsync();

        return userChatOrder;
    }

    public async Task<UserChatOrder?> UpdateChatOrderAsync(long orderId, int? promptTokens = null, int? completionTokens = null, UserChatOrderStatus? status = null)
    {
        var userChatOrder = await _context.UserChatOrders.FindAsync(orderId);
        if (userChatOrder == null)
        {
            return null;
        }

        if (promptTokens != null)
        {
            userChatOrder.PromptTokens = promptTokens.Value;
        }
        if (completionTokens != null)
        {
            userChatOrder.CompletionTokens = completionTokens.Value;
        }
        if (status != null)
        {
            userChatOrder.Status = status.Value;
        }
        await _context.SaveChangesAsync();

        await _chatOrderStatsService.UpdateAsync(userChatOrder.UserId, userChatOrder.Model, userChatOrder.PromptTokens, userChatOrder.CompletionTokens);

        return userChatOrder;
    }
}
