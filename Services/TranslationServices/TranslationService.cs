using System.Text;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Services.TranslationServices;

public interface ITranslationService
{
    /// <summary>
    /// 创建翻译订单
    /// </summary>
    /// <param name="type">翻译类型</param>
    /// <param name="content">需要翻译的内容</param>
    /// <returns>翻译订单</returns>
    Task<UserTranslationOrder> CreateTranslationOrderAsync(long userId, UserTranslationType type, string content);

    /// <summary>
    /// 翻译
    /// </summary>
    /// <param name="content">需要翻译的内容</param>
    /// <returns>翻译结果</returns>
    /// <param name="from">源语言</param>
    /// <param name="to">目标语言</param>
    /// <param name="domain">领域</param>
    virtual Task<string?> TranslateAsync(long userId, string content, string from, string to, string domain) => throw new NotImplementedException();
}

public class TranslationService(ApplicationDbContext context, HttpClient httpClient, IUserOrderService userOrderService, ITranslationOrderStatsService translationOrderStatsService) : ITranslationService
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly HttpClient _httpClient = httpClient;
    protected readonly ITranslationOrderStatsService _translationOrderStatsService = translationOrderStatsService;
    protected readonly IUserOrderService _userOrderService = userOrderService;

    public async Task<UserTranslationOrder> CreateTranslationOrderAsync(long userId, UserTranslationType type, string content)
    {
        var contentByteCount = Encoding.UTF8.GetByteCount(content);
        var userTranslationOrder = new UserTranslationOrder
        {
            Type = type,
            ContentLength = content.Length,
            ContentByteCount = contentByteCount,
        };
        await _context.UserTranslationOrders.AddAsync(userTranslationOrder);
        await _context.SaveChangesAsync();

        var userOrder = await _userOrderService.CreateUserOrderAsync(userId, UserOrderType.Translation, userTranslationOrder.Id);

        await _translationOrderStatsService.UpdateTranslationOrderStatsAsync(userOrder, userTranslationOrder);

        return userTranslationOrder;
    }
}
