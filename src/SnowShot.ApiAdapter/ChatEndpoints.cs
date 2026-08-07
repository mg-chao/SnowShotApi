using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using SnowShot.Api.Resources;
using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class ChatEndpoints
{
    private static readonly Action<ILogger, string, Exception?> StreamFailed =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(9101, nameof(StreamFailed)),
            "Chat stream failed after the response started for trace {TraceId}");

    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/v1/chat/completions", CompleteAsync)
            .WithName("ChatCompletions").WithTags("Chat").Accepts<ChatRequest>("application/json")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .Produces<PublicProblem>(StatusCodes.Status400BadRequest, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status409Conflict, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status413PayloadTooLarge, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status429TooManyRequests, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status502BadGateway, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")
            .Produces<PublicProblem>(StatusCodes.Status504GatewayTimeout, "application/problem+json");
        endpoints.MapGet("/api/v1/chat/models", Models).WithName("ChatModels").WithTags("Chat")
            .Produces<AppEnvelope>(StatusCodes.Status200OK);
        return endpoints;
    }

    private static IResult Models(IChatModelCatalog modelCatalog, PublicMessages messages)
    {
        return ApiResponse.Success(modelCatalog.Models.Select(model =>
            new ChatModelDescriptor(model.Model,
                messages[model.Model], model.Thinking, model.SupportVision)).ToArray(), messages);
    }

    private static async Task CompleteAsync(
        HttpContext context,
        ChatUseCase useCase,
        PublicMessages messages,
        ILoggerFactory loggerFactory,
        [FromHeader(Name = "X-Request-ID"), StringLength(64), RegularExpression(@"^[\x21-\x7E]+$")]
        string? suppliedRequestId,
        CancellationToken cancellationToken)
    {
        var read = await ApiResponse.ReadBytesAsync(context, 2 * 1024 * 1024,
            messages["Validation failed"], cancellationToken);
        if (read.Error is not null) { await read.Error.ExecuteAsync(context); return; }
        string model;
        try
        {
            using var document = JsonDocument.Parse(read.Value!);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("model", out var modelProperty) ||
                modelProperty.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(modelProperty.GetString()))
                throw new JsonException("A model is required.");
            model = modelProperty.GetString()!;
        }
        catch (JsonException)
        {
            await ApiResponse.Problem(context, StatusCodes.Status400BadRequest, "invalid_request",
                messages["Validation failed"]).ExecuteAsync(context);
            return;
        }
        var request = new ChatCommand(model, read.Value!);
        var errors = useCase.Validate(request);
        if (errors.Count > 0)
        {
            var modelError = errors.Any(value => value.Code == ValidationIssueCode.UnsupportedModel);
            await ApiResponse.Problem(context, StatusCodes.Status400BadRequest,
                modelError ? "model_not_found" : "invalid_request",
                modelError ? messages.Format("Model not found", model) : string.Join("; ", errors.Select(messages.Validation))).ExecuteAsync(context);
            return;
        }
        if (!RequestContextFactory.TryCreate(context, messages, out var requestContext, out var requestError))
        {
            await requestError!.ExecuteAsync(context);
            return;
        }

        await using var stream = useCase.ExecuteAsync(requestContext, request, cancellationToken).GetAsyncEnumerator(cancellationToken);
        var hasEvent = await stream.MoveNextAsync();
        if (!hasEvent)
        {
            await WriteFailureAsync(context, new(ApplicationErrorCode.ProviderFailure, "empty_stream"), messages);
            return;
        }
        if (stream.Current is ChatApplicationEvent.Failed initialFailure)
        {
            await WriteFailureAsync(context, initialFailure.Error, messages);
            return;
        }
        PrepareStream(context);
        try
        {
            while (hasEvent)
            {
                switch (stream.Current)
                {
                    case ChatApplicationEvent.Data data:
                        await context.Response.WriteAsync("data: ", cancellationToken);
                        await context.Response.Body.WriteAsync(data.Utf8Json, cancellationToken);
                        await context.Response.WriteAsync("\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                        break;
                    case ChatApplicationEvent.Completed:
                        await context.Response.WriteAsync("data: [DONE]\n\n", cancellationToken);
                        await context.Response.Body.FlushAsync(cancellationToken);
                        break;
                    case ChatApplicationEvent.Failed failed:
                        await ApiResponse.WriteSseProblemAsync(context, failed.Error, messages, cancellationToken);
                        return;
                }
                hasEvent = await stream.MoveNextAsync();
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (Exception exception)
        {
            StreamFailed(loggerFactory.CreateLogger("SnowShot.Api.ChatEndpoints"), context.TraceIdentifier, exception);
            await ApiResponse.WriteSseProblemAsync(context, StatusCodes.Status500InternalServerError,
                "internal_error", messages["Internal server error"], null, cancellationToken);
        }
    }

    private static async Task WriteFailureAsync(HttpContext context, ApplicationError error, PublicMessages messages)
    {
        await ApiResponse.ApplicationProblem(context, error, messages).ExecuteAsync(context);
    }

    private static void PrepareStream(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        context.Response.Headers.Append("X-Accel-Buffering", "no");
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    }
}
