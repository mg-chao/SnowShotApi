using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using SnowShotApi.Models;

namespace SnowShotApi.Controllers.TranslationControllers;

[ApiController]
[Route("api/v1/[controller]")]
public class TranslationController(
    ApplicationDbContext context,
    IStringLocalizer<AppControllerBase> localizer) : AppControllerBase(context, localizer)
{
    [HttpPost("translate")]
    public async Task TranslateAsync()
    {
        await DelatError(Response, 10001, _localizer["Upgrade tip"]);
        return;
    }

    [HttpGet("types")]
    public async Task GetTranslationTypes()
    {
        await DelatError(Response, 10001, _localizer["Upgrade tip"]);
        return;
    }
}