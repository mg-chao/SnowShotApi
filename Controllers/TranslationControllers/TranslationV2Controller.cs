using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using SnowShotApi.Data;
using SnowShotApi.Models;
using SnowShotApi.RequestValidations;
using SnowShotApi.Services.TranslationServices;
using SnowShotApi.Services.UserServices;

namespace SnowShotApi.Controllers.TranslationControllers;

public sealed class TranslateResponseData(
    List<TranslationContent> content,
    string? from = null,
    string? to = null)
{
    [JsonPropertyName("results")]
    public List<TranslationContent> Results { get; set; } = content;

    [JsonPropertyName("from")]
    public string? From { get; set; } = from;

    [JsonPropertyName("to")]
    public string? To { get; set; } = to;
}

public sealed class TranslationRequest
{
    [Required]
    [TranslationType]
    [JsonPropertyName("type")]
    public UserTranslationType Type { get; set; } = UserTranslationType.AI;

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

public sealed class TranslationTypeOption
{
    [JsonPropertyName("type")]
    public UserTranslationType Type { get; set; } = UserTranslationType.AI;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

[ApiController]
[Route("api/v2/translation")]
public sealed class TranslationV2Controller(
    ApplicationDbContext context,
    IStringLocalizer<AppControllerBase> localizer,
    IIpUserService ipUserService,
    ITranslationService translationService) : AppControllerBase(context, localizer)
{
    [HttpPost("translate")]
    public async Task<IActionResult> TranslateAsync(
        [FromBody] TranslationRequest request,
        CancellationToken cancellationToken)
    {
        var user = await ipUserService.GetUserAsync(HttpContext);
        if (user == null)
        {
            return Error(10001, _localizer["Cannot get client IP address"]);
        }

        var outcome = await translationService.TranslateAsync(
            new TranslationCommand(
                request.Type,
                request.Content,
                request.From,
                request.To,
                request.Domain),
            user.Id,
            cancellationToken);

        if (outcome.Status == TranslationOutcomeStatus.QuotaExceeded)
        {
            return Error(20001, _localizer["User translation limit reached"]);
        }

        if (outcome.Status == TranslationOutcomeStatus.Cancelled && cancellationToken.IsCancellationRequested)
        {
            return new EmptyResult();
        }

        if (outcome.Status != TranslationOutcomeStatus.Success || outcome.Result == null)
        {
            return Error(30001, _localizer["Failed to translate"]);
        }

        return Success(new TranslateResponseData(
            outcome.Result.Results,
            outcome.Result.From,
            outcome.Result.To));
    }

    [HttpGet("types")]
    public IActionResult GetTranslationTypes()
    {
        var translationTypes = new List<TranslationTypeOption>
        {
            new() { Type = UserTranslationType.AI, Name = _localizer["AI Translation"] },
        };

        return Success(translationTypes);
    }
}
