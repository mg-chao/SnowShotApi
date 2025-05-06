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
    [MaxLength(10000)]
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
    ITranslationOrderStatsService translationOrderStatsService,
    ITranslationService translationService) : AppControllerBase(context, localizer)
{
    [HttpPost("translate")]
    public async Task<IActionResult> TranslateAsync([FromBody] TranslationRequest request)
    {
        var user = await ipUserService.GetUserAsync(HttpContext);
        if (user == null)
        {
            return Error(10001, _localizer["Cannot get client IP address"]);
        }

        // 判断用户是否达到限额
        if (await translationOrderStatsService.IsLimitIpUserAsync(user.Id, UserTranslationType.Youdao))
        {
            return Error(20001, _localizer["User translation limit reached"]);
        }

        var result = await translationService.TranslateAsync(request, user.Id);

        if (result == null)
        {
            return Error(30001, _localizer["Failed to translate"]);
        }

        return Success(new TranslateResponseData(result.Content, result.From, result.To));
    }
}