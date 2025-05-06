using System.Text.Json.Serialization;
using SnowShotApi.Controllers.TranslationControllers;
using SnowShotApi.Data;
using SnowShotApi.Models;
using SnowShotApi.Services.OrderServices;

namespace SnowShotApi.Services.TranslationServices;

public class TranslateResult(string content, string from, string to, int? promptTokens = null, int? completionTokens = null)
{
    public string Content { get; set; } = content;
    public string From { get; set; } = from;
    public string To { get; set; } = to;
    public int PromptTokens { get; set; } = promptTokens ?? 0;
    public int CompletionTokens { get; set; } = completionTokens ?? 0;
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
    Task<TranslateResult?> TranslateAsync(TranslationRequest request, long userId);
}

public class TranslationService(HttpClient httpClient, ITranslationOrderService translationOrderService) : ITranslationService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static ITranslationService? GetInstance(UserTranslationType type, HttpClient httpClient, ITranslationOrderService translationOrderService)
    {
        return type switch
        {
            UserTranslationType.Youdao => new YoudaoTranslationService(httpClient),
            UserTranslationType.Deepseek => new DeepseekTranslationService(),
            _ => null,
        };
    }
    public async Task<TranslateResult?> TranslateAsync(TranslationRequest request, long userId)
    {
        var service = GetInstance(request.Type, httpClient, translationOrderService);
        if (service == null)
        {
            return null;
        }

        var translationOrder = await translationOrderService.CreateAsync(userId, request.Type, request.Content, request.From, request.To, request.Domain, 0, 0);
        var res = await service.TranslateAsync(request, userId);

        if (res == null)
        {
            await translationOrderService.UpdateAsync(translationOrder.Id, status: UserTranslationOrderStatus.Failed);
            return null;
        }
        await translationOrderService.UpdateAsync(translationOrder.Id, res.From, res.To, UserTranslationOrderStatus.Completed, res.PromptTokens, res.CompletionTokens);

        return res;
    }
}
