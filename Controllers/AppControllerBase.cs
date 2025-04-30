using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using System.Text.Json.Serialization;
using SnowShotApi.Resources;

namespace SnowShotApi.Controllers;

public class AppResponse(int code = 0, string message = "", object? data = null)
{
    [JsonPropertyName("code")]
    public int Code { get; set; } = code;

    [JsonPropertyName("success")]
    public bool Success { get; set; } = code == 0;

    [JsonPropertyName("message")]
    public string Message { get; set; } = message;

    [JsonPropertyName("data")]
    public object? Data { get; set; } = data;
}

public class AppControllerBase(ApplicationDbContext context, IStringLocalizer<AppControllerBase> localizer) : ControllerBase
{
    protected readonly ApplicationDbContext _context = context;
    protected readonly IStringLocalizer<AppControllerBase> _localizer = localizer;

    protected IActionResult Success(object data, string message = "")
    {
        return Ok(new AppResponse(0, message == "" ? _localizer["Request success"] : _localizer[message], data));
    }

    protected IActionResult Error(int code, string message)
    {
        return Ok(new AppResponse(code, message == "" ? _localizer["Request failed"] : _localizer[message]));
    }
}
