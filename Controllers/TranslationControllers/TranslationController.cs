using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using SnowShotApi.Services.UserServices;
using SnowShotApi.Services.TranslationServices;
using System.Text.Json.Serialization;
using SnowShotApi.Models;
using SnowShotApi.AppEnvs;

namespace SnowShotApi.Controllers.TranslationControllers;

public class TranslateResponseData(string result)
{
    [JsonPropertyName("result")]
    public string Result { get; set; } = result;
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
    public async Task<IActionResult> TranslateAsync([FromQuery] string content, [FromQuery] string from, [FromQuery] string to, [FromQuery] string domain)
    {
        var user = await ipUserService.GetUserAsync(HttpContext);
        if (user == null)
        {
            return Error(10001, _localizer["Cannot get client IP address"]);
        }

        // 判断用户是否达到限额
        var stats = await translationOrderStatsService.GetUserTranslationUserOrderStatsAsync(user.Id, UserTranslationType.Youdao);
        if (stats != null && stats.ContentByteCountSum >= _youdaoApiEnv.IpLimit)
        {
            return Error(20001, _localizer["User translation limit reached"]);
        }

        var result = await youdaoTranslationService.TranslateAsync(user.Id, content, from, to, domain);

        if (result == null)
        {
            return Error(30001, _localizer["Failed to translate"]);
        }

        return Success(new TranslateResponseData(result));
    }
}