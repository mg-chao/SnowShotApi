using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using SnowShotApi.Services.UserServices;
using SnowShotApi.Services.TranslationServices;
using System.Text.Json.Serialization;
using SnowShotApi.Models;
using SnowShotApi.AppEnvs;
using System.ComponentModel.DataAnnotations;
using SnowShotApi.RequestValidations;

namespace SnowShotApi.Controllers.TranslationControllers;

public class TranslationRequest()
{
    [Required]
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [Required]
    [TranslationLanguageCode(SupportAuto: true)]
    [JsonPropertyName("from")]
    public string From { get; set; } = string.Empty;

    [Required]
    [TranslationLanguageCode]
    [JsonPropertyName("to")]
    public string To { get; set; } = string.Empty;

    [TranslationDomain]
    [JsonPropertyName("domain")]
    public string Domain { get; set; } = string.Empty;
}

public class TranslateResponseData(string content, string from, string to)
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = content;

    [JsonPropertyName("from")]
    public string From { get; set; } = from;

    [JsonPropertyName("to")]
    public string To { get; set; } = to;
}

[ApiController]
[Route("api/v1/[controller]")]
public class TranslationController(
    ApplicationDbContext context,
    IStringLocalizer<AppControllerBase> localizer,
    IIpUserService ipUserService,
    IYoudaoTranslationService youdaoTranslationService,
    ITranslationOrderStatsService translationOrderStatsService) : AppControllerBase(context, localizer)
{
    private readonly YoudaoApiEnv _youdaoApiEnv = new();

    [HttpPost("translate")]
    public async Task<IActionResult> TranslateAsync([FromBody] TranslationRequest request)
    {
        var user = await ipUserService.GetUserAsync(HttpContext);
        if (user == null)
        {
            return Error(10001, _localizer["Cannot get client IP address"]);
        }

        // 判断用户是否达到限额
        var stats = await translationOrderStatsService.GetUserTranslationUserOrderStatsAsync(user.Id, UserTranslationType.Youdao);
        if (stats != null && stats.ContentLengthSum >= _youdaoApiEnv.ContentLengthLimit)
        {
            return Error(20001, _localizer["User translation limit reached"]);
        }

        var result = await youdaoTranslationService.TranslateAsync(user.Id, request.Content, request.From, request.To, request.Domain);

        if (result == null)
        {
            return Error(30001, _localizer["Failed to translate"]);
        }

        return Success(new TranslateResponseData(result.Content, result.From, result.To));
    }
}