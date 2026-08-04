using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Providers;

public sealed class OpenAiChatClient(
    IProviderHttpClientRegistry clients,
    ChatProviderOptions options,
    ProviderModelCatalog catalog,
    ServicePolicy policy,
    IDependencyHealth dependencyHealth,
    TimeProvider timeProvider) : IChatProviderClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async IAsyncEnumerable<ChatProviderEvent> StreamAsync(ChatProviderCommand command,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("provider.chat.dispatch");
        var request = command.Request;
        var id = command.AttemptId;
        var started = command.AttemptStartedAt;
        var access = catalog.Get(command.Access.LogicalModel, command.Access.AccessId);
        if (command.AttemptNumber > 1) SnowShotTelemetry.ProviderRetries.Add(1,
            new("kind", "chat"), new("model", request.Model), new("provider", access.Selection.Provider));
        using var message = new HttpRequestMessage(HttpMethod.Post, access.Endpoint)
        {
            Content = new ByteArrayContent(RewritePayload(request.Utf8Json.Span, access.Selection.UpstreamModel)),
        };
        message.Content.Headers.ContentType = new("application/json") { CharSet = "utf-8" };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access.ApiKey);
        message.Headers.TryAddWithoutValidation("X-Operation-ID", command.Operation.OperationId.ToString("N"));
        HttpResponseMessage? response = null;
        ChatProviderEvent.Failure? sendFailure = null;
        try
        {
            response = await clients.CreateClient(access.Selection).SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
        {
            dependencyHealth.Report("chat_provider", false);
            sendFailure = Failure(exception is OperationCanceledException ? "cancelled" : "network", null, null, false,
                AttemptDispatchState.Unknown, true);
        }
        if (sendFailure is not null) { yield return sendFailure; yield break; }
        using (var receivedResponse = response!)
        {
            if (!receivedResponse.IsSuccessStatusCode)
            {
                dependencyHealth.Report("chat_provider", false);
                await using var error = await receivedResponse.Content.ReadAsStreamAsync(cancellationToken);
                try { _ = await BoundedStreams.ReadAllAsync(error, options.MaximumErrorBodyBytes, cancellationToken); } catch (InvalidDataException) { }
                var status = (int)receivedResponse.StatusCode;
                yield return Failure($"provider_http_{status}", status, null, false, AttemptDispatchState.Dispatched,
                    status is 408 or 429 || status is >= 500 and <= 599);
                yield break;
            }
            await using var stream = await receivedResponse.Content.ReadAsStreamAsync(cancellationToken);
            var reader = new BoundedLineReader(stream, options.MaximumSseLineBytes);
            ChatUsage? usage = null;
            var sawDone = false;
            var delivered = false;
            while (true)
            {
                string? line;
                ChatProviderEvent.Failure? readFailure = null;
                try { line = await reader.ReadLineAsync(cancellationToken); }
                catch (InvalidDataException)
                {
                    dependencyHealth.Report("chat_provider", false);
                    await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.TransientFailure);
                    readFailure = Failure("invalid_stream", null, usage, usage is not null, AttemptDispatchState.Dispatched, false);
                    line = null;
                }
                if (readFailure is not null) { yield return readFailure; yield break; }
                if (line is null) break;
                if (line.Length == 0 || line.StartsWith(':')) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.TransientFailure);
                    yield return Failure("invalid_stream", null, usage, usage is not null, AttemptDispatchState.Dispatched, false);
                    yield break;
                }
                var data = line[5..].TrimStart();
                if (data == "[DONE]") { sawDone = true; continue; }
                byte[] frame;
                ChatProviderEvent.Failure? parseFailure = null;
                try
                {
                    using var document = JsonDocument.Parse(data);
                    var parsedUsage = ReadUsage(document.RootElement);
                    if (parsedUsage is not null)
                    {
                        _ = policy.Get(request.Model).Price.Calculate(parsedUsage.PromptTokens, parsedUsage.CompletionTokens);
                        usage = parsedUsage;
                    }
                    frame = Encoding.UTF8.GetBytes(data);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or OverflowException)
                {
                    dependencyHealth.Report("chat_provider", false);
                    await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.TransientFailure);
                    parseFailure = Failure("invalid_stream", null, usage, usage is not null, AttemptDispatchState.Dispatched, false);
                    frame = [];
                }
                if (parseFailure is not null) { yield return parseFailure; yield break; }
                delivered = true;
                yield return new ChatProviderEvent.Frame(frame);
            }
            var succeeded = delivered && sawDone && usage is not null;
            dependencyHealth.Report("chat_provider", succeeded);
            if (!succeeded)
            {
                await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.TransientFailure);
                yield return Failure("truncated_stream", null, usage, usage is not null,
                    AttemptDispatchState.Dispatched, !delivered);
                yield break;
            }
            await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.Success);
            yield return new ChatProviderEvent.Terminal(usage, true, true, "success",
                Attempt("success", null, usage, true, AttemptDispatchState.Dispatched));
        }

        ChatProviderEvent.Failure Failure(string outcome, int? status, ChatUsage? usage, bool known,
            AttemptDispatchState dispatchState, bool retryable) => new(outcome, retryable,
                Attempt(outcome, status, usage, known, dispatchState));

        ProviderAttempt Attempt(string outcome, int? status, ChatUsage? usage, bool known, AttemptDispatchState dispatchState)
        {
            var input = usage?.PromptTokens ?? 0;
            var output = usage?.CompletionTokens ?? 0;
            var cost = known ? policy.Get(request.Model).Price.Calculate(input, output) : NanoYuan.Zero;
            return new(id, command.Operation.OperationId, command.AttemptNumber, access.Selection.AttemptProvider, request.Model, outcome, status,
                input, output, cost, known, dispatchState, started, timeProvider.GetUtcNow());
        }
    }

    private static byte[] RewritePayload(ReadOnlySpan<byte> utf8Json, string upstreamModel)
    {
        using var document = JsonDocument.Parse(utf8Json.ToArray());
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            JsonElement? streamOptions = null;
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.NameEquals("model") || property.NameEquals("stream")) continue;
                if (property.NameEquals("stream_options")) { streamOptions = property.Value; continue; }
                property.WriteTo(writer);
            }
            writer.WriteString("model", upstreamModel);
            writer.WriteBoolean("stream", true);
            writer.WritePropertyName("stream_options");
            writer.WriteStartObject();
            if (streamOptions is { ValueKind: JsonValueKind.Object } options)
                foreach (var property in options.EnumerateObject())
                    if (!property.NameEquals("include_usage")) property.WriteTo(writer);
            writer.WriteBoolean("include_usage", true);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return output.ToArray();
    }

    private static ChatUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        if (!TryNonnegative(usage, "prompt_tokens", out var prompt) || !TryNonnegative(usage, "completion_tokens", out var completion) ||
            !TryNonnegative(usage, "total_tokens", out var total) || total != checked(prompt + completion))
            throw new InvalidDataException("Provider usage is incoherent.");
        if (prompt > AccountingLimits.MaximumUnitsPerDimension || completion > AccountingLimits.MaximumUnitsPerDimension)
            throw new InvalidDataException("Provider usage exceeds the accounting envelope.");
        var reasoning = 0L;
        if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reasoning_tokens", out var value) && (!value.TryGetInt64(out reasoning) || reasoning < 0 || reasoning > completion))
            throw new InvalidDataException("Provider reasoning usage is incoherent.");
        return new(prompt, completion, total, reasoning);
    }

    private static bool TryNonnegative(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value) && value >= 0;
    }
}
