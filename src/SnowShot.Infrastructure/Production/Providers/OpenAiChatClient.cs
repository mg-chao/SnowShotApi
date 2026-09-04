using System.Diagnostics.CodeAnalysis;
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
        var payload = RewritePayload(request.Utf8Json.Span, access.Selection.UpstreamModel,
            catalog.IsTranslationModel(command.Access.LogicalModel));
        using var message = new HttpRequestMessage(HttpMethod.Post, access.Endpoint)
        {
            Content = new ByteArrayContent(payload),
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
            sendFailure = Failure(exception is OperationCanceledException ? "cancelled" : "network", null, null,
                CostBasis.Unknown, AttemptDispatchState.Unknown, true);
        }
        if (sendFailure is not null) { yield return sendFailure; yield break; }
        var deliveredContentChars = 0L;
        using (var receivedResponse = response!)
        {
            if (!receivedResponse.IsSuccessStatusCode)
            {
                dependencyHealth.Report("chat_provider", false);
                await using var error = await receivedResponse.Content.ReadAsStreamAsync(cancellationToken);
                try { _ = await BoundedStreams.ReadAllAsync(error, options.MaximumErrorBodyBytes, cancellationToken); } catch (InvalidDataException) { }
                var status = (int)receivedResponse.StatusCode;
                var retryable = status is 408 or 429 || status is >= 500 and <= 599;
                // A rejected request never reached model execution (exact zero cost); a
                // timeout or server error may have executed the transmitted prompt, so the
                // prompt is billed as an estimate.
                yield return status is 408 or >= 500 and <= 599
                    ? Failure($"provider_http_{status}", status, null, CostBasis.Estimated,
                        AttemptDispatchState.Dispatched, retryable, policy.Estimation.EstimateInputTokens(payload.Length), 0)
                    : Failure($"provider_http_{status}", status, null, CostBasis.Exact,
                        AttemptDispatchState.Dispatched, retryable);
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
                    readFailure = usage is not null
                        ? Failure("invalid_stream", null, usage, CostBasis.Exact, AttemptDispatchState.Dispatched, false)
                        : EstimatedStreamFailure("invalid_stream", false);
                    line = null;
                }
                if (readFailure is not null) { yield return readFailure; yield break; }
                if (line is null) break;
                if (line.Length == 0 || line.StartsWith(':')) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.TransientFailure);
                    yield return usage is not null
                        ? Failure("invalid_stream", null, usage, CostBasis.Exact, AttemptDispatchState.Dispatched, false)
                        : EstimatedStreamFailure("invalid_stream", false);
                    yield break;
                }
                var data = line[5..].TrimStart();
                if (data == "[DONE]") { sawDone = true; continue; }
                byte[] frame;
                ChatProviderEvent.Failure? parseFailure = null;
                try
                {
                    using var document = JsonDocument.Parse(data);
                    var root = document.RootElement;
                    var parsedUsage = ReadUsage(root);
                    if (parsedUsage is not null)
                    {
                        _ = policy.Get(request.Model).Price.Calculate(parsedUsage.PromptTokens, parsedUsage.CompletionTokens);
                        usage = parsedUsage;
                    }
                    deliveredContentChars += ReadContentLength(root);
                    frame = Encoding.UTF8.GetBytes(data);
                }
                catch (Exception exception) when (exception is JsonException or InvalidDataException or OverflowException)
                {
                    dependencyHealth.Report("chat_provider", false);
                    await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.TransientFailure);
                    parseFailure = usage is not null
                        ? Failure("invalid_stream", null, usage, CostBasis.Exact, AttemptDispatchState.Dispatched, false)
                        : EstimatedStreamFailure("invalid_stream", false);
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
                yield return usage is not null
                    ? Failure("truncated_stream", null, usage, CostBasis.Exact, AttemptDispatchState.Dispatched, !delivered)
                    : EstimatedStreamFailure("truncated_stream", !delivered);
                yield break;
            }
            await clients.ReportAsync(access.Selection, ProviderCircuitOutcome.Success);
            yield return new ChatProviderEvent.Terminal(usage, true, CostBasis.Exact, "success",
                Attempt("success", null, usage, CostBasis.Exact, AttemptDispatchState.Dispatched));
        }

        // The stream was dispatched and content was observed, but the provider never sent
        // a usable usage frame: bill the transmitted prompt plus the delivered content as
        // an estimate instead of forfeiting to the reservation maximum.
        ChatProviderEvent.Failure EstimatedStreamFailure(string outcome, bool retryable) =>
            Failure(outcome, null, null, CostBasis.Estimated, AttemptDispatchState.Dispatched, retryable,
                policy.Estimation.EstimateInputTokens(payload.Length),
                policy.Estimation.EstimateOutputTokens(deliveredContentChars));

        ChatProviderEvent.Failure Failure(string outcome, int? status, ChatUsage? usage, CostBasis basis,
            AttemptDispatchState dispatchState, bool retryable, long estimatedInput = 0, long estimatedOutput = 0) =>
            new(outcome, retryable,
                Attempt(outcome, status, usage, basis, dispatchState, estimatedInput, estimatedOutput));

        ProviderAttempt Attempt(string outcome, int? status, ChatUsage? usage, CostBasis basis, AttemptDispatchState dispatchState,
            long estimatedInput = 0, long estimatedOutput = 0)
        {
            var input = usage?.PromptTokens ?? estimatedInput;
            var output = usage?.CompletionTokens ?? estimatedOutput;
            var cost = basis == CostBasis.Unknown ? NanoYuan.Zero
                : policy.Get(request.Model).Price.Calculate(input, output);
            return new(id, command.Operation.OperationId, command.AttemptNumber, access.Selection.AttemptProvider, request.Model, outcome, status,
                input, output, cost, basis, dispatchState, started, timeProvider.GetUtcNow());
        }
    }

    private static byte[] RewritePayload(ReadOnlySpan<byte> utf8Json, string upstreamModel, bool mergeSystemIntoUser)
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
                if (mergeSystemIntoUser && property.NameEquals("messages"))
                {
                    WriteMergedMessages(writer, property.Value);
                    continue;
                }
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

    // Translation models such as qwen-mt-flash reject the system role; system text
    // is merged into the first user turn so ordinary chat prompts keep working.
    // System messages whose content is not a plain string are forwarded unchanged.
    private static void WriteMergedMessages(Utf8JsonWriter writer, JsonElement messages)
    {
        writer.WritePropertyName("messages");
        if (messages.ValueKind != JsonValueKind.Array)
        {
            messages.WriteTo(writer);
            return;
        }
        var systemTexts = new List<string>();
        var hasUserTurn = false;
        foreach (var message in messages.EnumerateArray())
        {
            if (IsStringMessage(message, "system", out var content)) systemTexts.Add(content);
            else if (IsStringMessage(message, "user", out _)) hasUserTurn = true;
        }
        if (systemTexts.Count == 0)
        {
            messages.WriteTo(writer);
            return;
        }
        var merged = string.Join("\n\n", systemTexts);
        var mergedIntoUser = false;
        writer.WriteStartArray();
        foreach (var message in messages.EnumerateArray())
        {
            if (IsStringMessage(message, "system", out _))
            {
                // Without a user turn the merged system text becomes the user turn.
                if (!hasUserTurn && !mergedIntoUser)
                {
                    mergedIntoUser = true;
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WriteString("content", merged);
                    writer.WriteEndObject();
                }
                continue;
            }
            if (!mergedIntoUser && IsStringMessage(message, "user", out var userContent))
            {
                mergedIntoUser = true;
                writer.WriteStartObject();
                foreach (var property in message.EnumerateObject())
                {
                    if (property.NameEquals("content"))
                        writer.WriteString("content", merged + "\n\n" + userContent);
                    else property.WriteTo(writer);
                }
                writer.WriteEndObject();
                continue;
            }
            message.WriteTo(writer);
        }
        writer.WriteEndArray();
    }

    private static bool IsStringMessage(JsonElement message, string role, [NotNullWhen(true)] out string? content)
    {
        content = null;
        return message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("role", out var roleProperty) && roleProperty.ValueKind == JsonValueKind.String &&
            roleProperty.GetString() == role &&
            message.TryGetProperty("content", out var contentProperty) && contentProperty.ValueKind == JsonValueKind.String &&
            (content = contentProperty.GetString()) is not null;
    }

    /// <summary>
    /// Normalizes a provider usage frame instead of discarding it: a missing or
    /// contradictory total is derived from its components, a missing component is derived
    /// from the total, reasoning tokens are clamped to the completion, and implausible
    /// magnitudes yield null so the attempt settles from estimation evidence instead.
    /// </summary>
    private static ChatUsage? ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;
        var hasPrompt = TryNonnegative(usage, "prompt_tokens", out var prompt);
        var hasCompletion = TryNonnegative(usage, "completion_tokens", out var completion);
        var hasTotal = TryNonnegative(usage, "total_tokens", out var total);
        if (hasPrompt && hasCompletion)
        {
            total = checked(prompt + completion);
        }
        else if (hasTotal)
        {
            if (!hasPrompt && hasCompletion)
            {
                if (total < completion) return null;
                prompt = total - completion;
            }
            else if (hasPrompt && !hasCompletion)
            {
                if (total < prompt) return null;
                completion = total - prompt;
            }
            else return null;
            total = checked(prompt + completion);
        }
        else return null;
        if (prompt > AccountingLimits.MaximumUnitsPerDimension || completion > AccountingLimits.MaximumUnitsPerDimension)
            return null;
        var reasoning = 0L;
        if (usage.TryGetProperty("completion_tokens_details", out var details) && details.ValueKind == JsonValueKind.Object &&
            details.TryGetProperty("reasoning_tokens", out var value) && value.TryGetInt64(out reasoning) && reasoning > 0)
            reasoning = Math.Min(reasoning, completion);
        return new(prompt, completion, total, reasoning);
    }

    private static long ReadContentLength(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0) return 0;
        var first = choices[0];
        if (first.ValueKind != JsonValueKind.Object || !first.TryGetProperty("delta", out var delta) ||
            delta.ValueKind != JsonValueKind.Object) return 0;
        var length = 0L;
        if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            length += Count(content.GetString()!);
        if (delta.TryGetProperty("reasoning_content", out var reasoning) && reasoning.ValueKind == JsonValueKind.String)
            length += Count(reasoning.GetString()!);
        return length;
    }

    private static long Count(string value) => value.EnumerateRunes().LongCount();

    private static bool TryNonnegative(JsonElement element, string name, out long value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property) && property.TryGetInt64(out value) && value >= 0;
    }
}
