using SnowShotApi.Controllers.TranslationControllers;
using SnowShotApi.RequestValidations;
using OpenAI.Chat;
using System.ClientModel;
using SnowShotApi.Controllers;

namespace SnowShotApi.Services.TranslationServices;

/// <summary>
/// 大模型翻译服务的基础实现类
/// </summary>
public abstract class ModelTranslationServiceBase(string modelName) : AbstractModelTranslationService(modelName)
{
    /// <summary>
    /// 获取API密钥
    /// </summary>
    /// <returns>API密钥</returns>
    protected abstract string GetApiKey();

    /// <summary>
    /// 获取API基础URL
    /// </summary>
    /// <returns>API基础URL</returns>
    protected abstract string GetBaseUrl();

    /// <summary>
    /// 创建聊天客户端
    /// </summary>
    /// <returns>聊天客户端</returns>
    protected virtual ChatClient CreateChatClient()
    {
        return new ChatClient(_modelName, new ApiKeyCredential(GetApiKey()), new()
        {
            Endpoint = new Uri(GetBaseUrl()),
        });
    }

    protected virtual ChatCompletionOptions GetChatCompletionOptions()
    {
        return new()
        {
            MaxOutputTokenCount = 8192,
        };
    }

    /// <summary>
    /// 执行翻译
    /// </summary>
    /// <param name="request">翻译请求</param>
    /// <param name="response">HTTP响应</param>
    /// <param name="userId">用户ID</param>
    /// <returns>翻译结果</returns>
    public override async Task<TranslateResult?> TranslateAsync(TranslationRequest request, HttpResponse response, long userId)
    {
        try
        {
            var systemMessage = GetSystemMessage(request);
            var userMessage = $"{request.Content}";

            var client = CreateChatClient();

            var totalPromptTokens = 0;
            var totalCompletionTokens = 0;

            AppControllerBase.DelatInit(response);

            using var cts = new CancellationTokenSource(TranslationService.DefaultTimeout);

            await foreach (var completionUpdate in client.CompleteChatStreamingAsync(
                [new SystemChatMessage(systemMessage),
            new UserChatMessage(userMessage)],
                GetChatCompletionOptions(),
                cts.Token))
            {
                if (completionUpdate.ContentUpdate.Count > 0 && completionUpdate.ContentUpdate[0].Text != string.Empty)
                {
                    await AppControllerBase.DelatStreamSuccess(response, new TranslateResponseData(completionUpdate.ContentUpdate[0].Text));
                }

                if (completionUpdate.Usage != null)
                {
                    totalPromptTokens = completionUpdate.Usage.InputTokenCount;
                    totalCompletionTokens = completionUpdate.Usage.OutputTokenCount;

                    await AppControllerBase.DelatStreamSuccess(response, new TranslateResponseData(string.Empty, request.From, request.To));
                }
            }

            return new TranslateResult(request.From, request.To, totalPromptTokens, totalCompletionTokens);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }
}
