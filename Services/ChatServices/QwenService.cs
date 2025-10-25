using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Controllers;
using SnowShotApi.Controllers.ChatControllers;

namespace SnowShotApi.Services.ChatServices;

public interface IQwenService : IChatService
{
}

public class QwenService(
    HttpClient httpClient,
    IStringLocalizer<AppControllerBase> localizer) : BaseChatService(httpClient, localizer), IQwenService
{
    private readonly QwenApiEnv _qwenApiEnv = new();

    protected override string GetApiUrl()
    {
        return $"{_qwenApiEnv.BaseUrl}compatible-mode/v1/chat/completions";
    }

    protected override void SetRequestHeaders()
    {
        HttpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_qwenApiEnv.ApiKey}");
    }

    protected override StringContent CreateRequestContent(ChatRequest chatRequest)
    {
        var enableThinking = chatRequest.EnableThinking;

        var model = chatRequest.Model;
        return new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                messages = chatRequest.Messages,
                temperature = Math.Min(chatRequest.Temperature / 2, 1),
                max_tokens = chatRequest.MaxTokens,
                enable_thinking = enableThinking,
                thinking_budget = chatRequest.ThinkingBudgetTokens,
                stream_options = new
                {
                    include_usage = true,
                },
                stream = true,
            }),
            Encoding.UTF8,
            "application/json"
        );
    }
}
