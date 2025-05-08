using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using SnowShotApi.Services.UserServices;
using SnowShotApi.Services.TranslationServices;
using System.Text.Json.Serialization;
using SnowShotApi.Models;
using System.ComponentModel.DataAnnotations;
using SnowShotApi.RequestValidations;
using SnowShotApi.Services.OrderServices;

namespace SnowShotApi.Controllers.TranslationControllers;

public class TranslationRequest()
{

    [Required]
    [JsonPropertyName("type")]
    public UserTranslationType Type { get; set; } = UserTranslationType.Youdao;

    [Required]
    [MaxLength(5000)]
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

public class TranslateResponseData(string deltaContent, string? from = null, string? to = null)
{
    [JsonPropertyName("delta_content")]
    public string DeltaContent { get; set; } = deltaContent;

    [JsonPropertyName("from")]
    public string? From { get; set; } = from;

    [JsonPropertyName("to")]
    public string? To { get; set; } = to;
}

public class TranslationTypeOption
{
    [JsonPropertyName("type")]
    public UserTranslationType Type { get; set; } = UserTranslationType.Youdao;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

[ApiController]
[Route("api/v1/[controller]")]
public class TranslationController(
    ApplicationDbContext context,
    IStringLocalizer<AppControllerBase> localizer,
    IIpUserService ipUserService,
    ITranslationOrderStatsService translationOrderStatsService,
    ITranslationService translationService) : AppControllerBase(context, localizer)
{
    [HttpPost("translate")]
    public async Task TranslateAsync([FromBody] TranslationRequest request)
    {
        var user = await ipUserService.GetUserAsync(HttpContext);
        if (user == null)
        {
            await DelatError(Response, 10001, _localizer["Cannot get client IP address"]);
            return;
        }

        // 判断用户是否达到限额
        if (await translationOrderStatsService.IsLimitIpUserAsync(user.Id, UserTranslationType.Youdao))
        {
            await DelatError(Response, 20001, _localizer["User translation limit reached"]);
            return;
        }


        var result = await translationService.TranslateAsync(request, Response, user.Id);

        if (result == null)
        {
            await DelatError(Response, 30001, _localizer["Failed to translate"]);
            return;
        }
    }

    [HttpGet("types")]
    public IActionResult GetTranslationTypes()
    {
        var translationTypes = new List<TranslationTypeOption>
        {
            new() { Type = UserTranslationType.Youdao, Name = _localizer["Youdao"] },
            new() { Type = UserTranslationType.Deepseek, Name = _localizer["Deepseek"] },
            new() { Type = UserTranslationType.QwenTurbo, Name = $"{_localizer["Qwen"]} Turbo" },
            new() { Type = UserTranslationType.QwenPlus, Name = $"{_localizer["Qwen"]} Plus" },
            new() { Type = UserTranslationType.QwenMax, Name = $"{_localizer["Qwen"]} Max" }
        };

        return Success(translationTypes);
    }
}