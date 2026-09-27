using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Providers;

public sealed class LatexWorkerClient(
    IHttpClientFactory clients,
    LatexWorkerOptions options,
    ServicePolicy policy,
    IDependencyHealth dependencyHealth,
    TimeProvider timeProvider) : ILatexWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<LatexExtractionResult> ExtractAsync(LatexProviderCommand command, CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("provider.latex.dispatch");
        var operationId = command.Operation.OperationId;
        var input = command.Request;
        var id = command.AttemptId;
        var started = command.AttemptStartedAt;
        if (input.WebpData.Length > options.MaximumUploadBytes)
            return Result(LatexExtractionStatus.InvalidRequest, null, "request_too_large", true, null, AttemptDispatchState.NotDispatched);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/latex/extract")
        {
            Content = new ReadOnlyMemoryContent(input.WebpData),
        };
        request.Headers.TryAddWithoutValidation("X-Operation-ID", operationId.ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Request-ID", command.RequestId);
        request.Content.Headers.ContentType = new("image/webp");
        request.Content.Headers.ContentLength = input.WebpData.Length;
        try
        {
            using var response = await clients.CreateClient("latex").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await BoundedStreams.ReadAllAsync(stream, options.MaximumResponseBytes, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var success = JsonSerializer.Deserialize<LatexSuccess>(bytes, JsonOptions);
                return string.IsNullOrWhiteSpace(success?.Latex)
                    ? Result(LatexExtractionStatus.InferenceFailed, null, "invalid_latex", true, (int)response.StatusCode, AttemptDispatchState.Dispatched)
                    : Result(LatexExtractionStatus.Success, success.Latex, "success", true, (int)response.StatusCode, AttemptDispatchState.Dispatched);
            }
            var failure = JsonSerializer.Deserialize<LatexFailure>(bytes, JsonOptions);
            var status = (response.StatusCode, failure?.Error?.Code) switch
            {
                (HttpStatusCode.RequestEntityTooLarge, "payload_too_large") => LatexExtractionStatus.InvalidRequest,
                (HttpStatusCode.UnsupportedMediaType, "not_webp") => LatexExtractionStatus.InvalidRequest,
                (HttpStatusCode.UnprocessableEntity, "invalid_image" or "image_too_large") => LatexExtractionStatus.InvalidRequest,
                (HttpStatusCode.UnprocessableEntity, "no_formula") => LatexExtractionStatus.NoFormula,
                (HttpStatusCode.ServiceUnavailable, "worker_busy") => LatexExtractionStatus.Busy,
                (HttpStatusCode.InternalServerError, "inference_failed") => LatexExtractionStatus.InferenceFailed,
                _ => LatexExtractionStatus.Unavailable,
            };
            var known = status is not (LatexExtractionStatus.Unavailable or LatexExtractionStatus.Timeout);
            return Result(status, null, failure?.Error?.Code ?? "worker_http", known, (int)response.StatusCode, AttemptDispatchState.Dispatched);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(LatexExtractionStatus.Timeout, null, "timeout", false, null, AttemptDispatchState.Unknown);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
        {
            return Result(LatexExtractionStatus.Unavailable, null, exception is HttpRequestException ? "network" : "invalid_response", false,
                null, exception is HttpRequestException ? AttemptDispatchState.Unknown : AttemptDispatchState.Dispatched);
        }

        LatexExtractionResult Result(LatexExtractionStatus status, string? latex, string outcome, bool known, int? httpStatus,
            AttemptDispatchState dispatchState)
        {
            if (status == LatexExtractionStatus.Busy) SnowShotTelemetry.WorkerBusy.Add(1, new KeyValuePair<string, object?>("resource", Resources.LatexExtraction));
            if (outcome != "request_too_large")
                dependencyHealth.Report("latex_worker", status is LatexExtractionStatus.Success or LatexExtractionStatus.NoFormula or
                    LatexExtractionStatus.InvalidRequest or LatexExtractionStatus.Busy);
            var cost = status == LatexExtractionStatus.Success ? policy.Get(Resources.LatexExtraction).Price.Input : NanoYuan.Zero;
            var basis = known ? CostBasis.Exact : CostBasis.Unknown;
            var attempt = new ProviderAttempt(id, operationId, 1, "latex-worker", Resources.LatexExtraction, outcome,
                httpStatus, status == LatexExtractionStatus.Success ? 1 : 0, 0, cost, basis, dispatchState,
                started, timeProvider.GetUtcNow());
            return new(status, latex, attempt);
        }
    }

    private sealed class LatexSuccess { [JsonPropertyName("latex")] public string? Latex { get; init; } }
    private sealed class LatexFailure { [JsonPropertyName("error")] public LatexError? Error { get; init; } }
    private sealed class LatexError { [JsonPropertyName("code")] public string? Code { get; init; } }
}
