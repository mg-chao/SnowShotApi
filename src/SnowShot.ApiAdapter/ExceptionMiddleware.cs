using SnowShot.Api.Resources;
using SnowShot.Contracts;

namespace SnowShot.Api;

public sealed class ExceptionMiddleware(RequestDelegate next, ILogger<ExceptionMiddleware> logger)
{
    private static readonly Action<ILogger, string, Exception?> RequestFailed = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(9001, nameof(RequestFailed)), "Unhandled request failure {TraceId}");

    public async Task InvokeAsync(HttpContext context, PublicMessages messages)
    {
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            RequestFailed(logger, context.TraceIdentifier, exception);
            await ApiResponse.Problem(context, StatusCodes.Status500InternalServerError, "internal_error",
                messages["Internal server error"]).ExecuteAsync(context);
        }
    }
}
