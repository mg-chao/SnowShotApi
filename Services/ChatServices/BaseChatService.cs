using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Localization;
using SnowShotApi.Controllers;
using SnowShotApi.Controllers.ChatControllers;

namespace SnowShotApi.Services.ChatServices;

public abstract class BaseChatService(HttpClient httpClient, IStringLocalizer<AppControllerBase> localizer)
{
    protected readonly HttpClient HttpClient = httpClient;
    protected readonly IStringLocalizer<AppControllerBase> Localizer = localizer;

    protected abstract string GetApiUrl();
    protected abstract void SetRequestHeaders();
    protected abstract StringContent CreateRequestContent(ChatRequest chatRequest);
    protected abstract Task<ChatResult?> ProcessResponseStream(StreamReader reader, HttpResponse response);

    public async Task<ChatResult?> StreamChatCompletion(ChatRequest chatRequest, HttpResponse response, long userId)
    {
        try
        {
            var apiUrl = GetApiUrl();
            var content = CreateRequestContent(chatRequest);

            HttpClient.DefaultRequestHeaders.Clear();
            SetRequestHeaders();

            HttpClient.Timeout = ChatService.DefaultTimeout;

            var httpRequest = new HttpRequestMessage(HttpMethod.Post, apiUrl)
            {
                Content = content
            };

            // 使用带超时的取消令牌
            using var timeoutCts = new CancellationTokenSource(ChatService.DefaultTimeout);

            try
            {
                var responseMessage = await HttpClient.SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
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

                return await ProcessResponseStream(reader, response);
            }
            catch (OperationCanceledException)
            {
                // 处理超时异常
                await ChatService.ChatError(response, HttpStatusCode.RequestTimeout, new ChatError
                {
                    Message = Localizer["Request timed out"],
                    Type = "timeout_error",
                    Code = "request_timeout"
                });
                return null;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            await ChatService.ChatError(response, HttpStatusCode.InternalServerError, new ChatError
            {
                Message = Localizer["Request for Chat API failed"],
                Type = "internal_error",
                Code = "internal_error"
            });
            return null;
        }
    }
}