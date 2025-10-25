using SnowShotApi.Controllers.TranslationControllers;
using SnowShotApi.Models;
using SnowShotApi.Services.OrderServices;

namespace SnowShotApi.Services.TranslationServices;

public class TranslateResult(string from, string to)
{
    public string From { get; set; } = from;
    public string To { get; set; } = to;
}

public interface ITranslationService
{
    /// <summary>
    /// 翻译
    /// </summary>
    /// <param name="content">需要翻译的内容</param>
    /// <returns>翻译结果</returns>
    /// <param name="from">源语言</param>
    /// <param name="to">目标语言</param>
    /// <param name="domain">领域</param>
    Task<TranslateResult?> TranslateAsync(TranslationRequest request, HttpResponse response, long userId);
}

public class TranslationService(
    ITranslationOrderService translationOrderService,
    IYoudaoTranslationService youdaoTranslationService) : ITranslationService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private ITranslationService? GetInstance(UserTranslationType type)
    {
        return type switch
        {
            UserTranslationType.Youdao => youdaoTranslationService,
            _ => null,
        };
    }
    public async Task<TranslateResult?> TranslateAsync(TranslationRequest request, HttpResponse response, long userId)
    {
        var service = GetInstance(request.Type);
        if (service == null)
        {
            return null;
        }

        var translationOrder = await translationOrderService.CreateAsync(userId, request.Type, request.Content, request.From, request.To, request.Domain, 0, 0);
        var res = await service.TranslateAsync(request, response, userId);

        if (res == null)
        {
            await translationOrderService.UpdateAsync(translationOrder.Id, status: UserTranslationOrderStatus.Failed);
            return null;
        }
        await translationOrderService.UpdateAsync(translationOrder.Id, res.From, res.To, UserTranslationOrderStatus.Completed);

        return res;
    }
}
