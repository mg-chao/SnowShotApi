using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.WebUtilities;
using SnowShot.Api.Resources;
using SnowShot.Application;
using SnowShot.Contracts;

namespace SnowShot.Api;

internal static class ApiResponse
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public static IResult Success(object data, string message = "Request success") => Results.Json(AppEnvelope.Success(data, message), JsonOptions);

    public static IResult Problem(HttpContext context, int status, string code, string detail, TimeSpan? retryAfter = null)
    {
        int? retrySeconds = retryAfter is null ? null : Math.Max(1, (int)Math.Ceiling(retryAfter.Value.TotalSeconds));
        if (retrySeconds is not null) context.Response.Headers.RetryAfter = retrySeconds.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var problem = CreateProblem(context, status, code, detail, retrySeconds);
        return Results.Json(problem, JsonOptions, contentType: "application/problem+json", statusCode: status);
    }

    public static IResult ApplicationProblem(HttpContext context, ApplicationError error, PublicMessages messages)
    {
        var mapped = ApplicationErrorMapper.Map(error);
        return Problem(context, mapped.Status, mapped.Code, messages[mapped.MessageKey], mapped.Retryable ? error.RetryAfter : null);
    }

    public static async Task WriteSseProblemAsync(
        HttpContext context,
        ApplicationError error,
        PublicMessages messages,
        CancellationToken cancellationToken)
    {
        var mapped = ApplicationErrorMapper.Map(error);
        int? retrySeconds = mapped.Retryable && error.RetryAfter is not null
            ? Math.Max(1, (int)Math.Ceiling(error.RetryAfter.Value.TotalSeconds)) : null;
        await WriteSseProblemAsync(context, mapped.Status, mapped.Code, messages[mapped.MessageKey], retrySeconds, cancellationToken);
    }

    public static async Task WriteSseProblemAsync(
        HttpContext context,
        int status,
        string code,
        string detail,
        int? retryAfterSeconds,
        CancellationToken cancellationToken)
    {
        var problem = CreateProblem(context, status, code, detail, retryAfterSeconds);
        var payload = JsonSerializer.SerializeToUtf8Bytes(problem, JsonOptions);
        await context.Response.WriteAsync("event: error\ndata: ", cancellationToken);
        await context.Response.Body.WriteAsync(payload, cancellationToken);
        await context.Response.WriteAsync("\n\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }

    public static PublicProblem CreateProblem(HttpContext context, int status, string code, string detail, int? retryAfterSeconds = null) => new()
    {
        Type = $"urn:snowshot:problem:{code}",
        Title = ReasonPhrases.GetReasonPhrase(status),
        Status = status,
        Detail = detail,
        Instance = context.Request.Path.Value ?? "/",
        Code = code,
        TraceId = context.TraceIdentifier,
        RetryAfterSeconds = retryAfterSeconds,
    };

    public static async Task<(T? Value, IResult? Error)> ReadJsonAsync<T>(HttpContext context, long maximumBytes,
        string validationMessage, CancellationToken cancellationToken)
    {
        IResult Failure(int status) => Problem(context, status,
            status == StatusCodes.Status413PayloadTooLarge ? "payload_too_large" : "invalid_request", validationMessage);
        if (context.Request.ContentLength > maximumBytes) return (default, Failure(StatusCodes.Status413PayloadTooLarge));
        await using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (body.Length + read > maximumBytes) return (default, Failure(StatusCodes.Status413PayloadTooLarge));
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        try
        {
            body.Position = 0;
            var value = await JsonSerializer.DeserializeAsync<T>(body, JsonOptions, cancellationToken);
            return value is null ? (default, Failure(StatusCodes.Status400BadRequest)) : (value, null);
        }
        catch (JsonException) { return (default, Failure(StatusCodes.Status400BadRequest)); }
    }

    public static async Task<(byte[]? Value, IResult? Error)> ReadBytesAsync(HttpContext context, long maximumBytes,
        string validationMessage, CancellationToken cancellationToken)
    {
        IResult Failure(int status) => Problem(context, status,
            status == StatusCodes.Status413PayloadTooLarge ? "payload_too_large" : "invalid_request", validationMessage);
        if (context.Request.ContentLength > maximumBytes) return (null, Failure(StatusCodes.Status413PayloadTooLarge));
        await using var body = new MemoryStream();
        var buffer = ArrayPool<byte>.Shared.Rent(81_920);
        try
        {
            while (true)
            {
                var read = await context.Request.Body.ReadAsync(buffer, cancellationToken);
                if (read == 0) break;
                if (body.Length + read > maximumBytes) return (null, Failure(StatusCodes.Status413PayloadTooLarge));
                await body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
        return (body.ToArray(), null);
    }
}

public sealed record PublicProblem
{
    [JsonPropertyName("type")] public required string Type { get; init; }
    [JsonPropertyName("title")] public required string Title { get; init; }
    [JsonPropertyName("status")] public required int Status { get; init; }
    [JsonPropertyName("detail")] public required string Detail { get; init; }
    [JsonPropertyName("instance")] public required string Instance { get; init; }
    [JsonPropertyName("code")] public required string Code { get; init; }
    [JsonPropertyName("trace_id")] public required string TraceId { get; init; }
    [JsonPropertyName("retry_after_seconds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? RetryAfterSeconds { get; init; }
}
