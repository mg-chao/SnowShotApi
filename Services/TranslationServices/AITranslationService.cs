using System.Diagnostics;
using SnowShotApi.Services.TranslationServices.AI;

namespace SnowShotApi.Services.TranslationServices;

public enum AITranslationBatchStatus
{
    Success,
    Failed,
    Cancelled,
}

public sealed record AITranslationRequest(
    IReadOnlyList<string> Content,
    string From,
    string To,
    string Domain,
    long OrderId);

public sealed record AITranslationBatchResult(
    AITranslationBatchStatus Status,
    IReadOnlyList<string>? Translations = null)
{
    public static AITranslationBatchResult Success(IReadOnlyList<string> translations) =>
        new(AITranslationBatchStatus.Success, translations);

    public static AITranslationBatchResult Failed() => new(AITranslationBatchStatus.Failed);

    public static AITranslationBatchResult Cancelled() => new(AITranslationBatchStatus.Cancelled);
}

public interface IAITranslationService
{
    Task<AITranslationBatchResult> TranslateAsync(
        AITranslationRequest request,
        CancellationToken cancellationToken);
}

public sealed class AITranslationService(
    AITranslationOptions options,
    AITranslationModelRegistry modelRegistry,
    OpenAICompatibleTranslationClient client,
    AITranslationTelemetry telemetry,
    ILogger<AITranslationService> logger) : IAITranslationService
{
    public async Task<AITranslationBatchResult> TranslateAsync(
        AITranslationRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var activity = telemetry.StartBatchActivity(request.Content.Count);
        telemetry.RecordBatchStarted(request.Content.Count);

        if (request.From == request.To)
        {
            var unchanged = request.Content.ToArray();
            telemetry.RecordBatchCompleted("success", stopwatch.Elapsed);
            return AITranslationBatchResult.Success(unchanged);
        }

        using var batchCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        batchCancellation.CancelAfter(options.BatchTimeout);
        var translations = new string[request.Content.Count];
        var terminalFailure = 0;

        try
        {
            var tasks = request.Content.Select((content, index) => TranslateSegmentAsync(
                content,
                index,
                request,
                translations,
                batchCancellation,
                () => Interlocked.Exchange(ref terminalFailure, 1)));

            await Task.WhenAll(tasks);
            telemetry.RecordBatchCompleted("success", stopwatch.Elapsed);
            return AITranslationBatchResult.Success(translations);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            logger.LogInformation(
                "AI translation order {OrderId} was cancelled by the request",
                request.OrderId);
            telemetry.RecordBatchCompleted("cancelled", stopwatch.Elapsed);
            return AITranslationBatchResult.Cancelled();
        }
        catch (Exception exception) when (
            exception is OperationCanceledException or AITranslationSegmentException ||
            Volatile.Read(ref terminalFailure) == 1)
        {
            logger.LogWarning(
                "AI translation order {OrderId} failed after exhausting the model attempt budget",
                request.OrderId);
            telemetry.RecordBatchCompleted("failed", stopwatch.Elapsed);
            return AITranslationBatchResult.Failed();
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "AI translation order {OrderId} failed unexpectedly",
                request.OrderId);
            telemetry.RecordBatchCompleted("failed", stopwatch.Elapsed);
            return AITranslationBatchResult.Failed();
        }
    }

    private async Task TranslateSegmentAsync(
        string content,
        int index,
        AITranslationRequest request,
        string[] translations,
        CancellationTokenSource batchCancellation,
        Action recordTerminalFailure)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            translations[index] = content;
            return;
        }

        var attemptedModels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AITranslationAttemptResult? lastResult = null;

        for (var attempt = 1; attempt <= options.MaxAttemptsPerSegment; attempt++)
        {
            if (attemptedModels.Count >= modelRegistry.Count)
            {
                attemptedModels.Clear();
            }

            using var lease = await modelRegistry.AcquireAsync(attemptedModels, batchCancellation.Token);
            var runtime = lease.Model;
            attemptedModels.Add(runtime.Id);
            var attemptStopwatch = Stopwatch.StartNew();

            lastResult = await client.TranslateAsync(
                runtime,
                new AITranslationSegmentRequest(
                    content,
                    request.From,
                    request.To,
                    request.Domain,
                    request.OrderId),
                batchCancellation.Token);

            telemetry.RecordAttempt(runtime.Id, attempt, lastResult, attemptStopwatch.Elapsed);

            if (lastResult.IsSuccess)
            {
                runtime.RecordSuccess();
                translations[index] = lastResult.Translation!;
                logger.LogInformation(
                    "AI translation order {OrderId} segment {SegmentIndex} completed with model {ModelId} on attempt {Attempt} in {ElapsedMs} ms",
                    request.OrderId,
                    index,
                    runtime.Id,
                    attempt,
                    attemptStopwatch.Elapsed.TotalMilliseconds);
                return;
            }

            if (runtime.RecordFailure(lastResult))
            {
                telemetry.RecordCircuitOpened(runtime.Id);
                logger.LogWarning(
                    "AI translation model {ModelId} circuit opened after {FailureKind}",
                    runtime.Id,
                    lastResult.FailureKind);
            }
            logger.LogWarning(
                "AI translation order {OrderId} segment {SegmentIndex} model {ModelId} failed on attempt {Attempt} with {FailureKind} and HTTP {StatusCode}",
                request.OrderId,
                index,
                runtime.Id,
                attempt,
                lastResult.FailureKind,
                lastResult.StatusCode);

            if (attempt < options.MaxAttemptsPerSegment)
            {
                await Task.Delay(GetRetryDelay(attempt, lastResult.RetryAfter), batchCancellation.Token);
            }
        }

        recordTerminalFailure();
        batchCancellation.Cancel();
        throw new AITranslationSegmentException(index, lastResult?.FailureKind ?? AITranslationFailureKind.ProviderUnavailable);
    }

    private TimeSpan GetRetryDelay(int attempt, TimeSpan? retryAfter)
    {
        var maximum = TimeSpan.FromSeconds(options.MaxRetryDelaySeconds);
        if (retryAfter is { } providerDelay)
        {
            return providerDelay > maximum ? maximum : providerDelay;
        }

        var milliseconds = Math.Min(
            maximum.TotalMilliseconds,
            200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 250));
        return TimeSpan.FromMilliseconds(milliseconds);
    }

    private sealed class AITranslationSegmentException(int segmentIndex, AITranslationFailureKind failureKind)
        : Exception($"AI translation segment {segmentIndex} failed with {failureKind}.");
}
