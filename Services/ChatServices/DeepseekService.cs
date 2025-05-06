using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Localization;
using SnowShotApi.AppEnvs;
using SnowShotApi.Controllers;
using SnowShotApi.Controllers.ChatControllers;

namespace SnowShotApi.Services.ChatServices;

public interface IDeepseekService : IChatService
{
}

public class DeepseekService(
    HttpClient httpClient,
    IStringLocalizer<AppControllerBase> localizer) : IDeepseekService
{
    private readonly HttpClient _httpClient = httpClient;
    private readonly IStringLocalizer<AppControllerBase> _localizer = localizer;
    private readonly DeepseekApiEnv _deepseekApiEnv = new();

    public async Task<ChatResult?> StreamChatCompletion(ChatRequest chatRequest, HttpResponse response, long userId)
    {
        try
        {
            var apiUrl = $"{_deepseekApiEnv.BaseUrl}/chat/completions";

            var content = new StringContent(
                JsonSerializer.Serialize(new
                {
                    model = chatRequest.Model,
                    messages = chatRequest.Messages,
                    temperature = chatRequest.Temperature,
                    max_tokens = chatRequest.MaxTokens,
                    stream = true,
                }),
                Encoding.UTF8,
                "application/json"
            );

            _httpClient.DefaultRequestHeaders.Clear();
            _httpClient.DefaultRequestHeaders.Add("Authorization", $"Bearer {_deepseekApiEnv.Key}");
            
            _httpClient.Timeout = ChatService.DefaultTimeout;

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };

            // 使用带超时的取消令牌
            using var timeoutCts = new CancellationTokenSource(ChatService.DefaultTimeout);
            
            try
            {
                var responseMessage = await _httpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
                if (responseMessage.StatusCode != HttpStatusCode.OK)
                {
                    await ChatService.ChatError(response, responseMessage);
                    return null;
                }

                response.ContentType = "text/event-stream";
                response.Headers.CacheControl = "no-cache";
                response.Headers.Connection = "keep-alive";

                response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();

                using var stream = await responseMessage.Content.ReadAsStreamAsync(timeoutCts.Token);
                using var reader = new StreamReader(stream);

                var lastValidLine = string.Empty;
                while (!reader.EndOfStream)
                {
                    if (timeoutCts.IsCancellationRequested)
                    {
                        break;
                    }
                    
                    var line = await reader.ReadLineAsync();
                    if (string.IsNullOrEmpty(line)) continue;

                    await response.WriteAsync($"{line}\n\n");
                    if (!line.StartsWith("data: [DONE]"))
                    {
                        lastValidLine = line;
                    }
                    await response.Body.FlushAsync();
                }

                if (string.IsNullOrEmpty(lastValidLine))
                {
                    await ChatService.ChatError(response, HttpStatusCode.InternalServerError, new ChatError
                    {
                        Message = _localizer["Failed to read Tokens"],
                        Type = "internal_error",
                        Code = "internal_error"
                    });
                    return null;
                }

                var result = JsonSerializer.Deserialize<ChatResult>(lastValidLine[6..]);
                return result;
            }
            catch (OperationCanceledException)
            {
                // 处理超时异常
                await ChatService.ChatError(response, HttpStatusCode.RequestTimeout, new ChatError
                {
                    Message = _localizer["Request timed out"],
                    Type = "timeout_error",
                    Code = "request_timeout"
                });
                return null;
            }
        }
        catch (Exception)
        {
            await ChatService.ChatError(response, HttpStatusCode.InternalServerError, new ChatError
            {
                Message = _localizer["Request for Chat API failed"],
                Type = "internal_error",
                Code = "internal_error"
            });
            return null;
        }
    }
}
