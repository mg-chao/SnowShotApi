using SnowShot.Application;

namespace SnowShot.Api;

internal static class ApplicationErrorMapper
{
    public static ProblemMapping Map(ApplicationError error) => error.Code switch
    {
        ApplicationErrorCode.InvalidRequest => new(StatusCodes.Status400BadRequest, "invalid_request", "Validation failed", false),
        ApplicationErrorCode.IdentityUnavailable => new(StatusCodes.Status503ServiceUnavailable, "identity_unavailable", "Cannot get client IP address", true),
        ApplicationErrorCode.DuplicateRequest => new(StatusCodes.Status409Conflict, "duplicate_request", "Duplicate request", false),
        ApplicationErrorCode.LeaseLost => new(StatusCodes.Status409Conflict, "ownership_lost", "Request ownership lost", false),
        ApplicationErrorCode.RateLimited => new(StatusCodes.Status429TooManyRequests, "rate_limited", "Request limit reached", true),
        ApplicationErrorCode.QueueFull => new(StatusCodes.Status429TooManyRequests, "queue_full", "Request queue full", true),
        ApplicationErrorCode.AllowanceExhausted => new(StatusCodes.Status429TooManyRequests, "allowance_exhausted", "Request limit reached", true),
        ApplicationErrorCode.OperatorBudgetExhausted => new(StatusCodes.Status503ServiceUnavailable, "operator_budget_exhausted", "Operator budget exhausted", true),
        ApplicationErrorCode.DependencyUnavailable => new(StatusCodes.Status503ServiceUnavailable, "dependency_unavailable", "Service unavailable", true),
        ApplicationErrorCode.PolicyUnavailable => new(StatusCodes.Status503ServiceUnavailable, "policy_unavailable", "Service unavailable", false),
        ApplicationErrorCode.PayloadTooLarge => new(StatusCodes.Status413PayloadTooLarge, "payload_too_large", "Payload too large", false),
        ApplicationErrorCode.DeadlineExceeded => new(StatusCodes.Status504GatewayTimeout, "deadline_exceeded", "Request deadline exceeded", false),
        _ => new(StatusCodes.Status502BadGateway, "provider_failure", "Upstream service failed", false),
    };
}

internal sealed record ProblemMapping(int Status, string Code, string MessageKey, bool Retryable);
