using System.Text;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.TranslationServices;

public class TranslateResult(string content, string from, string to)
{
    public string Content { get; set; } = content;
    public string From { get; set; } = from;
    public string To { get; set; } = to;
}

public interface ITranslationService
{
    /// <summary>
    /// 创建翻译订单
    /// </summary>
    /// <param name="type">翻译类型</param>
    /// <param name="content">需要翻译的内容</param>
    /// <returns>翻译订单</returns>
    Task<UserTranslationOrder> CreateTranslationOrderAsync(long userId, UserTranslationType type, string content, string from, string to, string domain);

    /// <summary>
    /// 更新翻译订单的 From To
    /// </summary>
    /// <param name="orderId">翻译订单ID</param>
    /// <param name="from">源语言</param>
    /// <param name="to">目标语言</param>
    /// <returns>翻译订单</returns>
    Task<UserTranslationOrder?> UpdateTranslationOrderAsync(long orderId, string from, string to);


    /// <summary>
    /// 翻译
    /// </summary>
    /// <param name="content">需要翻译的内容</param>
    /// <returns>翻译结果</returns>
    /// <param name="from">源语言</param>
    /// <param name="to">目标语言</param>
    /// <param name="domain">领域</param>
    virtual Task<TranslateResult?> TranslateAsync(long userId, string content, string from, string to, string domain) => throw new NotImplementedException();
}

public class TranslationService(ApplicationDbContext context, HttpClient httpClient, IUserOrderService userOrderService, ITranslationOrderStatsService translationOrderStatsService) : ITranslationService
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly HttpClient _httpClient = httpClient;
    protected readonly ITranslationOrderStatsService _translationOrderStatsService = translationOrderStatsService;
    protected readonly IUserOrderService _userOrderService = userOrderService;

    public async Task<UserTranslationOrder> CreateTranslationOrderAsync(long userId, UserTranslationType type, string content, string from, string to, string domain)
    {
        var userTranslationOrder = new UserTranslationOrder
        {
            Type = type,
            ContentLength = content.Length,
            From = from,
            To = to,
            Domain = domain,
        };
        await _context.UserTranslationOrders.AddAsync(userTranslationOrder);
        await _context.SaveChangesAsync();

        var userOrder = await _userOrderService.CreateUserOrderAsync(userId, UserOrderType.Translation, userTranslationOrder.Id);

        await _translationOrderStatsService.UpdateTranslationOrderStatsAsync(userOrder, userTranslationOrder);

        return userTranslationOrder;
    }

    public async Task<UserTranslationOrder?> UpdateTranslationOrderAsync(long orderId, string from, string to)
    {
        var userTranslationOrder = await _context.UserTranslationOrders.FindAsync(orderId);
        if (userTranslationOrder == null)
        {
            return null;
        }

        userTranslationOrder.From = from;
        userTranslationOrder.To = to;
        await _context.SaveChangesAsync();

        return userTranslationOrder;
    }
}
