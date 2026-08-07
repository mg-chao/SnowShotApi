using SnowShot.Application;
using SnowShot.Contracts;
using SnowShot.Api.Resources;

namespace SnowShot.Api;

internal static class RequestContextFactory
{
    public static bool TryCreate(HttpContext context, PublicMessages messages, out RequestContext requestContext, out IResult? error)
    {
        var suppliedValues = context.Request.Headers["X-Request-ID"];
        if (suppliedValues.Count > 0 &&
            (suppliedValues.Count != 1 || string.IsNullOrWhiteSpace(suppliedValues[0]) || suppliedValues[0]!.Length > 64 ||
             suppliedValues[0]!.Any(character => character is < '!' or > '~')))
        {
            requestContext = default!;
            error = ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                messages["Request ID invalid"]);
            return false;
        }
        var requestId = suppliedValues.Count == 0 ? context.TraceIdentifier : suppliedValues[0]!;
        requestContext = new(context.Connection.RemoteIpAddress?.ToString(), requestId, context.TraceIdentifier);
        error = null;
        return true;
    }

}
