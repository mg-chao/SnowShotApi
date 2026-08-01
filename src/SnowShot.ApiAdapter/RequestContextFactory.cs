using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class RequestContextFactory
{
    public static bool TryCreate(HttpContext context, out RequestContext requestContext, out IResult? error)
    {
        var suppliedValues = context.Request.Headers["X-Request-ID"];
        if (suppliedValues.Count > 0 &&
            (suppliedValues.Count != 1 || string.IsNullOrWhiteSpace(suppliedValues[0]) || suppliedValues[0]!.Length > 64 ||
             suppliedValues[0]!.Any(character => character is < '!' or > '~')))
        {
            requestContext = default!;
            error = ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                "X-Request-ID must contain exactly one value with at most 64 visible ASCII characters.");
            return false;
        }
        var requestId = suppliedValues.Count == 0 ? context.TraceIdentifier : suppliedValues[0]!;
        requestContext = new(context.Connection.RemoteIpAddress?.ToString(), requestId, context.TraceIdentifier);
        error = null;
        return true;
    }

}
