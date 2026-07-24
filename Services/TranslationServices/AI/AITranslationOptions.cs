using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace SnowShotApi.Services.TranslationServices.AI;

public enum AITranslationThinkingMode
{
    None,
    EnableThinkingFalse,
    DisabledObject,
}

public enum AITranslationUserIdentifierField
{
    None,
    User,
    UserId,
}

public enum AITranslationResponseFormat
{
    PromptOnly,
    JsonObject,
}

public enum AITranslationMaxTokensField
{
    MaxTokens,
    MaxCompletionTokens,
}

public sealed class AITranslationProviderOptions
{
    public string Endpoint { get; set; } = string.Empty;
    public string ApiKey { get; set; } = string.Empty;
    public AITranslationThinkingMode ThinkingMode { get; set; }
    public AITranslationUserIdentifierField UserIdentifierField { get; set; }
}

public sealed class AITranslationModelOptions
{
    public string ProviderId { get; set; } = string.Empty;
    public string Model { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int Weight { get; set; } = 1;
    public int MaxConcurrency { get; set; } = 8;
    public int MaxOutputTokens { get; set; } = 16384;
    public AITranslationResponseFormat ResponseFormat { get; set; } = AITranslationResponseFormat.JsonObject;
    public AITranslationMaxTokensField MaxTokensField { get; set; } = AITranslationMaxTokensField.MaxTokens;
}

public sealed class AITranslationOptions
{
    public const string SectionName = "AITranslation";

    public int ContentLengthLimit { get; set; } = 1_000_000;
    public int AttemptTimeoutSeconds { get; set; } = 60;
    public int BatchTimeoutSeconds { get; set; } = 180;
    public int MaxAttemptsPerSegment { get; set; } = 3;
    public int MaxRetryDelaySeconds { get; set; } = 5;
    public int CircuitFailureThreshold { get; set; } = 3;
    public int CircuitBreakSeconds { get; set; } = 30;
    public int StaleReservationGraceSeconds { get; set; } = 300;
    public int ReservationCleanupIntervalSeconds { get; set; } = 60;
    public string QuotaTimeZoneId { get; set; } = "Asia/Shanghai";
    public Dictionary<string, AITranslationProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AITranslationModelOptions> Models { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan AttemptTimeout => TimeSpan.FromSeconds(AttemptTimeoutSeconds);
    public TimeSpan BatchTimeout => TimeSpan.FromSeconds(BatchTimeoutSeconds);
    public TimeSpan CircuitBreakDuration => TimeSpan.FromSeconds(CircuitBreakSeconds);
    public TimeSpan StaleReservationGrace => TimeSpan.FromSeconds(StaleReservationGraceSeconds);
    public TimeSpan ReservationCleanupInterval => TimeSpan.FromSeconds(ReservationCleanupIntervalSeconds);
}

public sealed class AITranslationOptionsValidator(IWebHostEnvironment environment)
    : IValidateOptions<AITranslationOptions>
{
    private static readonly Regex IdentifierPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant);

    public ValidateOptionsResult Validate(string? name, AITranslationOptions options)
    {
        var errors = new List<string>();

        ValidateRange(options.ContentLengthLimit, 1, 10_000_000, nameof(options.ContentLengthLimit), errors);
        ValidateRange(options.AttemptTimeoutSeconds, 1, 600, nameof(options.AttemptTimeoutSeconds), errors);
        ValidateRange(options.BatchTimeoutSeconds, options.AttemptTimeoutSeconds, 1800, nameof(options.BatchTimeoutSeconds), errors);
        ValidateRange(options.MaxAttemptsPerSegment, 1, 10, nameof(options.MaxAttemptsPerSegment), errors);
        ValidateRange(options.MaxRetryDelaySeconds, 0, 60, nameof(options.MaxRetryDelaySeconds), errors);
        ValidateRange(options.CircuitFailureThreshold, 2, 100, nameof(options.CircuitFailureThreshold), errors);
        ValidateRange(options.CircuitBreakSeconds, 1, 600, nameof(options.CircuitBreakSeconds), errors);
        ValidateRange(options.StaleReservationGraceSeconds, 1, 3600, nameof(options.StaleReservationGraceSeconds), errors);
        ValidateRange(options.ReservationCleanupIntervalSeconds, 10, 3600, nameof(options.ReservationCleanupIntervalSeconds), errors);

        try
        {
            TranslationQuotaClock.ResolveTimeZone(options.QuotaTimeZoneId);
        }
        catch (TimeZoneNotFoundException)
        {
            errors.Add($"{nameof(options.QuotaTimeZoneId)} '{options.QuotaTimeZoneId}' was not found.");
        }
        catch (InvalidTimeZoneException)
        {
            errors.Add($"{nameof(options.QuotaTimeZoneId)} '{options.QuotaTimeZoneId}' is invalid.");
        }

        var enabledModels = options.Models.Where(entry => entry.Value.Enabled).ToArray();
        if (enabledModels.Length == 0)
        {
            errors.Add("At least one enabled AI translation model must be configured.");
        }

        foreach (var (providerId, provider) in options.Providers)
        {
            if (!IdentifierPattern.IsMatch(providerId))
            {
                errors.Add($"Provider ID '{providerId}' must contain only letters, numbers, '.', '_', or '-' and be at most 64 characters.");
            }

            if (!Uri.TryCreate(provider.Endpoint, UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp))
            {
                errors.Add($"Provider '{providerId}' must have an absolute HTTP(S) endpoint.");
            }
            else if (!environment.IsDevelopment() && endpoint.Scheme != Uri.UriSchemeHttps)
            {
                errors.Add($"Provider '{providerId}' must use HTTPS outside development.");
            }
        }

        foreach (var (modelId, model) in enabledModels)
        {
            if (!IdentifierPattern.IsMatch(modelId))
            {
                errors.Add($"Model ID '{modelId}' must contain only letters, numbers, '.', '_', or '-' and be at most 64 characters.");
            }

            if (string.IsNullOrWhiteSpace(model.Model))
            {
                errors.Add($"Model '{modelId}' must define a remote model name.");
            }

            if (!options.Providers.TryGetValue(model.ProviderId, out var provider))
            {
                errors.Add($"Model '{modelId}' references unknown provider '{model.ProviderId}'.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                errors.Add($"Provider '{model.ProviderId}' requires an API key because model '{modelId}' is enabled.");
            }

            ValidateRange(model.Weight, 1, 1000, $"Models:{modelId}:Weight", errors);
            ValidateRange(model.MaxConcurrency, 1, 2500, $"Models:{modelId}:MaxConcurrency", errors);
            ValidateRange(model.MaxOutputTokens, 1, 384000, $"Models:{modelId}:MaxOutputTokens", errors);
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static void ValidateRange(int value, int minimum, int maximum, string field, List<string> errors)
    {
        if (value < minimum || value > maximum)
        {
            errors.Add($"{field} must be between {minimum} and {maximum}.");
        }
    }
}
