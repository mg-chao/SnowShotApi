using System.Text.Json;
using SnowShot.Domain;

namespace SnowShot.Application;

public static class CommandValidator
{
    public static IReadOnlyList<string> Validate(ChatCommand request, ServicePolicy policy, IChatModelCatalog modelCatalog)
    {
        var errors = new List<string>();
        if (!modelCatalog.Contains(request.Model) ||
            !policy.ResourcePolicies.Any(value => value.Resource == request.Model))
            errors.Add($"Unsupported model: {request.Model}");
        return errors;
    }

    public static IReadOnlyList<string> Validate(TranslationCommand request)
    {
        var errors = new List<string>();
        if (request.Content.Count is < 1 or > 50) errors.Add("Content must contain between 1 and 50 items.");
        var totalLength = 0L;
        foreach (var (content, index) in request.Content.Select((value, index) => (value, index)))
        {
            if (content is null) errors.Add($"Content[{index}] cannot be null.");
            else totalLength += content.Length;
        }
        if (totalLength > 5_000) errors.Add("Content total length cannot exceed 5000 characters.");
        if (request.From != "auto" && !Capabilities.TranslationLanguages.Contains(request.From)) errors.Add($"Unsupported language code: {request.From}");
        if (!Capabilities.TranslationLanguages.Contains(request.To)) errors.Add($"Unsupported language code: {request.To}");
        if (!Capabilities.TranslationDomains.Contains(request.Domain)) errors.Add($"Unsupported domain: {request.Domain}");
        return errors;
    }

}

public enum ApplicationErrorCode
{
    InvalidRequest,
    PayloadTooLarge,
    IdentityUnavailable,
    RateLimited,
    QueueFull,
    DependencyUnavailable,
    AllowanceExhausted,
    OperatorBudgetExhausted,
    DuplicateRequest,
    ProviderFailure,
    DeadlineExceeded,
    LeaseLost,
    PolicyUnavailable,
}

public sealed record ApplicationError(ApplicationErrorCode Code, string Detail, TimeSpan? RetryAfter = null);

public sealed record ApplicationResult<T>(T? Value, ApplicationError? Error)
{
    public bool IsSuccess => Error is null;
}

public static class ApplicationResult
{
    public static ApplicationResult<T> Success<T>(T value) => new(value, null);
    public static ApplicationResult<T> Failure<T>(ApplicationError error) => new(default, error);
}
