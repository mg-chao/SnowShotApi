using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using System.Text.Json.Serialization;
using SnowShotApi.Resources;
using System.Net;
using System.Text.Json;

namespace SnowShotApi.Controllers;

public class AppResponse(int code = 0, string message = "", object? data = null)
{
    [JsonPropertyName("code")]
    public int Code { get; set; } = code;

    [JsonPropertyName("message")]
    public string Message { get; set; } = message;

    [JsonPropertyName("data")]
    public object? Data { get; set; } = data;
}

public class AppControllerBase(ApplicationDbContext context, IStringLocalizer<AppControllerBase> localizer) : ControllerBase
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly IStringLocalizer<AppControllerBase> _localizer = localizer;

    private static AppResponse GetSuccessResponse(object data, string message = "")
    {
        return new AppResponse(0, message == "" ? "Request success" : message, data);
    }

    private static AppResponse GetErrorResponse(int code, string message)
    {
        return new AppResponse(code, message == "" ? "Request failed" : message);
    }

    protected IActionResult Success(object data, string message = "")
    {
        return Ok(GetSuccessResponse(data, message));
    }

    protected IActionResult Error(int code, string message)
    {
        return Ok(GetErrorResponse(code, message));
    }

    public static async Task DelatError(HttpResponse response, int code, string message)
    {
        response.StatusCode = (int)HttpStatusCode.OK;
        response.ContentType = "application/json";
        await response.WriteAsync(JsonSerializer.Serialize(GetErrorResponse(code, message)));
        await response.Body.FlushAsync();
    }

    public static void DelatInit(HttpResponse response)
    {
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        response.Headers.Connection = "keep-alive";

        response.HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpResponseBodyFeature>()?.DisableBuffering();
    }

    public static async Task DelatStream(HttpResponse response, object data)
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(data)}\n\n");
        await response.Body.FlushAsync();
    }

    public static async Task DelatStream(HttpResponse response, string data)
    {
        await response.WriteAsync($"data: {data}\n\n");
        await response.Body.FlushAsync();
    }

    public static async Task DelatStreamSuccess<T>(HttpResponse response, T data) where T : class
    {
        await response.WriteAsync($"data: {JsonSerializer.Serialize(GetSuccessResponse(data))}\n\n");
        await response.Body.FlushAsync();
    }

}