using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp.Html.Parser;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Providers;

public sealed class TableWorkerClient(
    IHttpClientFactory clients,
    TableWorkerOptions options,
    ServicePolicy policy,
    IDependencyHealth dependencyHealth,
    TimeProvider timeProvider) : ITableWorkerClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HashSet<string> AllowedElements = new(StringComparer.OrdinalIgnoreCase)
        { "html", "body", "table", "thead", "tbody", "tfoot", "colgroup", "col", "tr", "th", "td", "br" };
    private static readonly HashSet<string> AllowedAttributes = new(StringComparer.OrdinalIgnoreCase)
        { "rowspan", "colspan" };

    public async Task<TableExtractionResult> ExtractAsync(TableProviderCommand command, CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("provider.table.dispatch");
        var operationId = command.Operation.OperationId;
        var input = command.Request;
        var id = command.AttemptId;
        var started = command.AttemptStartedAt;
        if (input.WebpData.Length > options.MaximumUploadBytes)
            return Result(TableExtractionStatus.InvalidRequest, null, "request_too_large", true, null, AttemptDispatchState.NotDispatched);
        using var request = new HttpRequestMessage(HttpMethod.Post, "v2/table/extract")
        {
            Content = new ReadOnlyMemoryContent(input.WebpData),
        };
        request.Headers.TryAddWithoutValidation("X-Operation-ID", operationId.ToString("N"));
        request.Headers.TryAddWithoutValidation("X-Request-ID", command.RequestId);
        request.Content.Headers.ContentType = new("image/webp");
        request.Content.Headers.ContentLength = input.WebpData.Length;
        try
        {
            using var response = await clients.CreateClient("table").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var bytes = await BoundedStreams.ReadAllAsync(stream, options.MaximumResponseBytes, cancellationToken);
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var success = JsonSerializer.Deserialize<TableSuccess>(bytes, JsonOptions);
                return string.IsNullOrWhiteSpace(success?.Html) || !IsSafeTableHtml(success.Html)
                    ? Result(TableExtractionStatus.InferenceFailed, null, "invalid_html", true, (int)response.StatusCode, AttemptDispatchState.Dispatched)
                    : Result(TableExtractionStatus.Success, success.Html, "success", true, (int)response.StatusCode, AttemptDispatchState.Dispatched);
            }
            var failure = JsonSerializer.Deserialize<TableFailure>(bytes, JsonOptions);
            var status = (response.StatusCode, failure?.Error?.Code) switch
            {
                (HttpStatusCode.RequestEntityTooLarge, "payload_too_large") => TableExtractionStatus.InvalidRequest,
                (HttpStatusCode.UnsupportedMediaType, "not_webp") => TableExtractionStatus.InvalidRequest,
                (HttpStatusCode.UnprocessableEntity, "invalid_image" or "image_too_large") => TableExtractionStatus.InvalidRequest,
                (HttpStatusCode.UnprocessableEntity, "no_table") => TableExtractionStatus.NoTable,
                (HttpStatusCode.ServiceUnavailable, "worker_busy") => TableExtractionStatus.Busy,
                (HttpStatusCode.InternalServerError, "inference_failed") => TableExtractionStatus.InferenceFailed,
                _ => TableExtractionStatus.Unavailable,
            };
            var known = status is not (TableExtractionStatus.Unavailable or TableExtractionStatus.Timeout);
            return Result(status, null, failure?.Error?.Code ?? "worker_http", known, (int)response.StatusCode, AttemptDispatchState.Dispatched);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Result(TableExtractionStatus.Timeout, null, "timeout", false, null, AttemptDispatchState.Unknown);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException)
        {
            return Result(TableExtractionStatus.Unavailable, null, exception is HttpRequestException ? "network" : "invalid_response", false,
                null, exception is HttpRequestException ? AttemptDispatchState.Unknown : AttemptDispatchState.Dispatched);
        }

        TableExtractionResult Result(TableExtractionStatus status, string? html, string outcome, bool known, int? httpStatus,
            AttemptDispatchState dispatchState)
        {
            if (status == TableExtractionStatus.Busy) SnowShotTelemetry.WorkerBusy.Add(1);
            if (outcome != "request_too_large")
                dependencyHealth.Report("table_worker", status is TableExtractionStatus.Success or TableExtractionStatus.NoTable or
                    TableExtractionStatus.InvalidRequest or TableExtractionStatus.Busy);
            var cost = status == TableExtractionStatus.Success ? policy.Get(Resources.TableExtraction).Price.Input : NanoYuan.Zero;
            var attempt = new ProviderAttempt(id, operationId, 1, "table-worker", Resources.TableExtraction, outcome,
                httpStatus, status == TableExtractionStatus.Success ? 1 : 0, 0, cost, known, dispatchState,
                started, timeProvider.GetUtcNow());
            return new(status, html, attempt);
        }
    }

    private static bool IsSafeTableHtml(string html)
    {
        try
        {
            var document = new HtmlParser().ParseDocument(html);
            if (document.QuerySelectorAll("table").Length != 1)
                return false;
            foreach (var element in document.All)
            {
                // AngleSharp creates an empty head element even when the worker did not emit one.
                if (element.LocalName.Equals("head", StringComparison.OrdinalIgnoreCase)) continue;
                if (!AllowedElements.Contains(element.LocalName)) return false;
                foreach (var attribute in element.Attributes)
                {
                    if (!AllowedAttributes.Contains(attribute.LocalName) ||
                        !int.TryParse(attribute.Value, out var span) || span is < 1 or > 1_000) return false;
                }
            }
            return true;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException) { return false; }
    }

    private sealed class TableSuccess { [JsonPropertyName("html")] public string? Html { get; init; } }
    private sealed class TableFailure { [JsonPropertyName("error")] public TableError? Error { get; init; } }
    private sealed class TableError { [JsonPropertyName("code")] public string? Code { get; init; } }
}
