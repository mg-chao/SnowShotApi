using System.Text.Json;
using SnowShot.Domain;

namespace SnowShot.Application;

public static class CommandValidator
{
    public static IReadOnlyList<ValidationIssue> Validate(ChatCommand request, ServicePolicy policy, IChatModelCatalog modelCatalog)
    {
        var errors = new List<ValidationIssue>();
        if (!modelCatalog.Contains(request.Model) ||
            !policy.ResourcePolicies.Any(value => value.Resource == request.Model))
            errors.Add(new(ValidationIssueCode.UnsupportedModel, request.Model));
        return errors;
    }

    public static IReadOnlyList<ValidationIssue> Validate(TranslationCommand request)
    {
        var errors = new List<ValidationIssue>();
        if (request.Content.Count is < 1 or > 50) errors.Add(new(ValidationIssueCode.ContentCount));
        var totalLength = 0L;
        foreach (var (content, index) in request.Content.Select((value, index) => (value, index)))
        {
            if (content is null) errors.Add(new(ValidationIssueCode.NullContentItem, Index: index));
            else totalLength += content.Length;
        }
        if (totalLength > 5_000) errors.Add(new(ValidationIssueCode.ContentTooLong));
        if (request.From != "auto" && !Capabilities.TranslationLanguages.Contains(request.From))
            errors.Add(new(ValidationIssueCode.UnsupportedLanguage, request.From));
        if (!Capabilities.TranslationLanguages.Contains(request.To))
            errors.Add(new(ValidationIssueCode.UnsupportedLanguage, request.To));
        if (!Capabilities.TranslationDomains.Contains(request.Domain))
            errors.Add(new(ValidationIssueCode.UnsupportedDomain, request.Domain));
        return errors;
    }

}

public enum ValidationIssueCode
{
    UnsupportedModel,
    ContentCount,
    NullContentItem,
    ContentTooLong,
    UnsupportedLanguage,
    UnsupportedDomain,
}

public sealed record ValidationIssue(ValidationIssueCode Code, string? Value = null, int? Index = null)
{
    public override string ToString() => Code switch
    {
        ValidationIssueCode.UnsupportedModel => $"Unsupported model: {Value}",
        ValidationIssueCode.ContentCount => "Content must contain between 1 and 50 items.",
        ValidationIssueCode.NullContentItem => $"Content[{Index}] cannot be null.",
        ValidationIssueCode.ContentTooLong => "Content total length cannot exceed 5000 characters.",
        ValidationIssueCode.UnsupportedLanguage => $"Unsupported language code: {Value}",
        ValidationIssueCode.UnsupportedDomain => $"Unsupported domain: {Value}",
        _ => "Validation failed.",
    };

    public bool Contains(string value, StringComparison comparisonType) =>
        ToString().Contains(value, comparisonType);
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
