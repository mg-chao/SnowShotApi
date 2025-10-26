using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using SnowShotApi.Services.UserServices;
using System.Text.Json.Serialization;
using SnowShotApi.Models;
using System.ComponentModel.DataAnnotations;
using SnowShotApi.RequestValidations;
using SnowShotApi.Services.OrderServices;
using SnowShotApi.Services.TranslationServices;

namespace SnowShotApi.Controllers.TranslationControllers;

public class TranslateResponseData(List<TranslationContent> content, string? from = null, string? to = null)
{
    [JsonPropertyName("results")]
    public List<TranslationContent> Results { get; set; } = content;

    [JsonPropertyName("from")]
    public string? From { get; set; } = from;

    [JsonPropertyName("to")]
    public string? To { get; set; } = to;
}

public class TranslationRequest()
{
    [Required]
    [TranslationType]
    [JsonPropertyName("type")]
    public UserTranslationType Type { get; set; } = UserTranslationType.Youdao;

    [Required]
    [MaxLength(50)]
    [MinLength(1)]
    [MaxLengthElement(5000)]
    [JsonPropertyName("content")]
    public List<string> Content { get; set; } = [];

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

public class TranslationTypeOption
{
    [JsonPropertyName("type")]
    public UserTranslationType Type { get; set; } = UserTranslationType.Youdao;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

[ApiController]
[Route("api/v2/translation")]
public class TranslationV2Controller(
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
        if (await translationOrderStatsService.IsLimitIpUserAsync(user.Id, request.Type))
        {
            return Error(20001, _localizer["User translation limit reached"]);
        }

        var result = await translationService.TranslateAsync(request, Response, user.Id);

        if (result == null)
        {
            return Error(30001, _localizer["Failed to translate"]);
        }

        return Success(new TranslateResponseData(result.Results, result.From, result.To));
    }

    [HttpGet("types")]
    public IActionResult GetTranslationTypes()
    {
        var translationTypes = new List<TranslationTypeOption>
        {
            new() { Type = UserTranslationType.Youdao, Name = _localizer["Youdao"] },
        };

        return Success(translationTypes);
    }
}