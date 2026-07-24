using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace SnowShotApi.Services.TranslationServices.AI;

public sealed class AITranslationTelemetry : IDisposable
{
    public const string MeterName = "SnowShotApi.Translation";
    public const string ActivitySourceName = "SnowShotApi.Translation";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly ActivitySource _activitySource = new(ActivitySourceName, "1.0.0");
    private readonly Counter<long> _batches;
    private readonly Counter<long> _segments;
    private readonly Counter<long> _attempts;
    private readonly Counter<long> _failovers;
    private readonly Counter<long> _failures;
    private readonly Counter<long> _circuitsOpened;
    private readonly Counter<long> _circuitRejections;
    private readonly Histogram<double> _attemptDuration;
    private readonly Histogram<double> _batchDuration;
    private readonly UpDownCounter<long> _activeRequests;

    public AITranslationTelemetry()
    {
        _batches = _meter.CreateCounter<long>("snowshot.translation.batches");
        _segments = _meter.CreateCounter<long>("snowshot.translation.segments");
        _attempts = _meter.CreateCounter<long>("snowshot.translation.attempts");
        _failovers = _meter.CreateCounter<long>("snowshot.translation.failovers");
        _failures = _meter.CreateCounter<long>("snowshot.translation.failures");
        _circuitsOpened = _meter.CreateCounter<long>("snowshot.translation.circuits_opened");
        _circuitRejections = _meter.CreateCounter<long>("snowshot.translation.circuit_rejections");
        _attemptDuration = _meter.CreateHistogram<double>("snowshot.translation.attempt.duration", "ms");
        _batchDuration = _meter.CreateHistogram<double>("snowshot.translation.batch.duration", "ms");
        _activeRequests = _meter.CreateUpDownCounter<long>("snowshot.translation.model.active_requests");
    }

    public Activity? StartBatchActivity(int segmentCount)
    {
        var activity = _activitySource.StartActivity("AI translation batch", ActivityKind.Internal);
        activity?.SetTag("translation.segment_count", segmentCount);
        return activity;
    }

    public void RecordBatchStarted(int segmentCount)
    {
        _segments.Add(segmentCount);
    }

    public void RecordBatchCompleted(string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "outcome", outcome } };
        _batches.Add(1, tags);
        _batchDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordAttempt(string modelId, int attempt, AITranslationAttemptResult result, TimeSpan duration)
    {
        var tags = new TagList
        {
            { "model.id", modelId },
            { "outcome", result.IsSuccess ? "success" : "failure" },
            { "failure.kind", result.IsSuccess ? "none" : result.FailureKind.ToString() },
        };

        _attempts.Add(1, tags);
        _attemptDuration.Record(duration.TotalMilliseconds, tags);
        if (!result.IsSuccess)
        {
            _failures.Add(1, tags);
            if (result.FailureKind == AITranslationFailureKind.CircuitOpen)
            {
                _circuitRejections.Add(1, tags);
            }
        }

        if (attempt > 1)
        {
            _failovers.Add(1, new KeyValuePair<string, object?>("model.id", modelId));
        }
    }

    public void ChangeActiveRequests(string modelId, long delta)
    {
        _activeRequests.Add(delta, new KeyValuePair<string, object?>("model.id", modelId));
    }

    public void RecordCircuitOpened(string modelId)
    {
        _circuitsOpened.Add(1, new KeyValuePair<string, object?>("model.id", modelId));
    }

    public void RecordCircuitRejection(string modelId)
    {
        _circuitRejections.Add(1, new KeyValuePair<string, object?>("model.id", modelId));
    }

    public void Dispose()
    {
        _activitySource.Dispose();
        _meter.Dispose();
    }
}
