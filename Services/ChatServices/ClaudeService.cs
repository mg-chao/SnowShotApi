using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Controllers;
using SnowShotApi.Controllers.ChatControllers;

namespace SnowShotApi.Services.ChatServices;

public interface IClaudeService : IChatService
{
}

public class ClaudeService(
    HttpClient httpClient,
    IStringLocalizer<AppControllerBase> localizer) : BaseChatService(httpClient, localizer), IClaudeService
{
    private readonly ClaudeApiEnv _claudeApiEnv = new();

    protected override string GetApiUrl()
    {
        return $"{_claudeApiEnv.BaseUrl}v1/messages";
    }

    protected override void SetRequestHeaders()
    {
        HttpClient.DefaultRequestHeaders.Add("x-api-key", _claudeApiEnv.ApiKey);
        HttpClient.DefaultRequestHeaders.Add("anthropic-version", _claudeApiEnv.ApiVersion);
    }

    protected override StringContent CreateRequestContent(ChatRequest chatRequest)
    {
        var enableThinking = chatRequest.Model.EndsWith("_thinking");

        var model = chatRequest.Model;
        if (enableThinking)
        {
            model = model[..^9];
        }

        var maxTokens = chatRequest.MaxTokens;
        if (enableThinking)
        {
            maxTokens = Math.Max(maxTokens + 1024, chatRequest.ThinkingBudgetTokens);
        }

        object thinkingParams = enableThinking ? new
        {
            type = "enabled",
            budget_tokens = chatRequest.ThinkingBudgetTokens
        } : new
        {
            type = "disabled"
        };

        var temperature = Math.Max(Math.Min(chatRequest.Temperature / 2, 1), 0);
        if (enableThinking)
        {
            temperature = 1;
        }

        return new StringContent(
            JsonSerializer.Serialize(new
            {
                model,
                messages = chatRequest.Messages,
                max_tokens = maxTokens,
                temperature,
                thinking = thinkingParams,
                stream = true
            }),
            Encoding.UTF8,
            "application/json"
        );
    }
}
