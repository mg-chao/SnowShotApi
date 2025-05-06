using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;
using SnowShotApi.Controllers;
using SnowShotApi.Controllers.ChatControllers;
using SnowShotApi.Models;
using SnowShotApi.Services.OrderServices;

namespace SnowShotApi.Services.ChatServices;

public class ChatError
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("param")]
    public string? Param { get; set; }

    [JsonPropertyName("code")]
    public string Code { get; set; } = "";
}

public class ChatErrorResponse
{
    [JsonPropertyName("error")]
    public ChatError Error { get; set; } = new();
}

public class ChatUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }

    [JsonPropertyName("total_tokens")]
    public int TotalTokens { get; set; }
}

public class ChatResult
{
    [JsonPropertyName("usage")]
    public ChatUsage Usage { get; set; } = new();
}

public interface IChatService
{
    Task<ChatResult?> StreamChatCompletion(ChatRequest chatRequest, HttpResponse response, long userId);
}

public class ChatService(
    HttpClient httpClient,
    IStringLocalizer<AppControllerBase> localizer,
    IChatOrderService chatOrderService
) : IChatService
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    public static async Task ChatError(HttpResponse response, HttpResponseMessage errorResponse)
    {
        response.StatusCode = (int)errorResponse.StatusCode;
        response.ContentType = errorResponse.Content.Headers.ContentType?.ToString() ?? "application/json";
        await response.WriteAsync(await errorResponse.Content.ReadAsStringAsync());
        await response.Body.FlushAsync();
    }
    public static async Task ChatError(HttpResponse response, HttpStatusCode statusCode, ChatError error)
    {
        response.StatusCode = (int)statusCode;
        response.ContentType = "application/json";
        await response.WriteAsync(JsonSerializer.Serialize(new ChatErrorResponse
        {
            Error = error
        }));
        await response.Body.FlushAsync();
    }

    private static IChatService? GetInstance(ChatRequest chatRequest, HttpClient httpClient, IStringLocalizer<AppControllerBase> localizer)
    {
        return chatRequest.Model switch
        {
            "deepseek-chat" or "deepseek-reasoner" => new DeepseekService(httpClient, localizer),
            _ => null,
        };
    }

    public async Task<ChatResult?> StreamChatCompletion(ChatRequest chatRequest, HttpResponse response, long userId)
    {
        var service = GetInstance(chatRequest, httpClient, localizer);
        if (service == null)
        {
            await ChatError(response, HttpStatusCode.BadRequest, new ChatError
            {
                Message = localizer["Invalid model"],
                Type = "invalid_model",
                Code = "invalid_model"
            });
            return null;
        }

        var chatOrder = await chatOrderService.CreateChatOrderAsync(userId, chatRequest.Model);
        var result = await service.StreamChatCompletion(chatRequest, response, userId);
        if (result == null)
        {
            await chatOrderService.UpdateChatOrderAsync(chatOrder.Id, status: UserChatOrderStatus.Failed);
            return result;
        }

        await chatOrderService.UpdateChatOrderAsync(chatOrder.Id, promptTokens: result.Usage.PromptTokens, completionTokens: result.Usage.CompletionTokens, status: UserChatOrderStatus.Completed);
        return result;
    }
}
