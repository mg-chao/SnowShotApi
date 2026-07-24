namespace SnowShotApi.Services.TranslationServices.AI;

public sealed class AITranslationModelRegistry : IDisposable
{
    private readonly object _selectionLock = new();
    private readonly AITranslationModelRuntime[] _models;
    private readonly AITranslationTelemetry _telemetry;
    private long _tieBreaker;

    public AITranslationModelRegistry(AITranslationOptions options, AITranslationTelemetry telemetry)
    {
        _telemetry = telemetry;
        _models = options.Models
            .Where(entry => entry.Value.Enabled)
            .Select(entry =>
            {
                var provider = options.Providers[entry.Value.ProviderId];
                return new AITranslationModelRuntime(
                    entry.Key,
                    entry.Value,
                    provider,
                    options,
                    telemetry);
            })
            .OrderBy(model => model.Id, StringComparer.Ordinal)
            .ToArray();
    }

    public int Count => _models.Length;

    public void Dispose()
    {
        foreach (var model in _models)
        {
            model.Dispose();
        }
    }

    public async Task<AITranslationModelLease> AcquireAsync(
        IReadOnlySet<string> excludedModelIds,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task<AITranslationModelLease>? leaseTask = null;
            var waitFor = TimeSpan.Zero;

            lock (_selectionLock)
            {
                var now = DateTimeOffset.UtcNow;
                var candidates = _models
                    .Where(model => !excludedModelIds.Contains(model.Id))
                    .ToArray();

                var available = candidates
                    .Where(model => model.IsAvailable(now))
                    .ToArray();

                if (available.Length > 0)
                {
                    var offset = (int)((Interlocked.Increment(ref _tieBreaker) & long.MaxValue) % available.Length);
                    var selected = available
                        .OrderBy(model => model.NormalizedLoad)
                        .ThenBy(model => (Array.IndexOf(available, model) - offset + available.Length) % available.Length)
                        .First();
                    leaseTask = selected.AcquireAsync(cancellationToken);
                }
                else if (candidates.Length > 0)
                {
                    waitFor = candidates
                        .Select(model => model.TimeUntilAvailable(now))
                        .Where(delay => delay > TimeSpan.Zero)
                        .DefaultIfEmpty(TimeSpan.FromMilliseconds(100))
                        .Min();
                }
                else
                {
                    throw new InvalidOperationException("No AI translation model is eligible for selection.");
                }
            }

            if (leaseTask != null)
            {
                try
                {
                    return await leaseTask;
                }
                catch (AITranslationModelUnavailableException exception)
                {
                    _telemetry.RecordCircuitRejection(exception.ModelId);
                    continue;
                }
            }

            await Task.Delay(
                waitFor > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : waitFor,
                cancellationToken);
        }
    }
}

public sealed class AITranslationModelRuntime : IDisposable
{
    private readonly SemaphoreSlim _concurrency;
    private readonly AITranslationOptions _globalOptions;
    private readonly AITranslationTelemetry _telemetry;
    private int _queued;
    private int _active;
    private int _consecutiveFailures;
    private long _unavailableUntilUtcTicks;

    public AITranslationModelRuntime(
        string id,
        AITranslationModelOptions model,
        AITranslationProviderOptions provider,
        AITranslationOptions globalOptions,
        AITranslationTelemetry telemetry)
    {
        Id = id;
        Model = model;
        Provider = provider;
        _globalOptions = globalOptions;
        _telemetry = telemetry;
        _concurrency = new SemaphoreSlim(model.MaxConcurrency, model.MaxConcurrency);
    }

    public string Id { get; }
    public AITranslationModelOptions Model { get; }
    public AITranslationProviderOptions Provider { get; }
    public double NormalizedLoad =>
        (Volatile.Read(ref _active) + Volatile.Read(ref _queued)) /
        (double)(Model.MaxConcurrency * Model.Weight);

    public bool IsAvailable(DateTimeOffset now)
    {
        return Volatile.Read(ref _unavailableUntilUtcTicks) <= now.UtcTicks;
    }

    public TimeSpan TimeUntilAvailable(DateTimeOffset now)
    {
        var ticks = Volatile.Read(ref _unavailableUntilUtcTicks) - now.UtcTicks;
        return ticks <= 0 ? TimeSpan.Zero : TimeSpan.FromTicks(ticks);
    }

    public async Task<AITranslationModelLease> AcquireAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _queued);
        try
        {
            await _concurrency.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _queued);
        }

        if (!IsAvailable(DateTimeOffset.UtcNow))
        {
            _concurrency.Release();
            throw new AITranslationModelUnavailableException(Id);
        }

        Interlocked.Increment(ref _active);
        _telemetry.ChangeActiveRequests(Id, 1);
        return new AITranslationModelLease(this);
    }

    public void RecordSuccess()
    {
        Interlocked.Exchange(ref _consecutiveFailures, 0);
        Interlocked.Exchange(ref _unavailableUntilUtcTicks, 0);
    }

    public bool RecordFailure(AITranslationAttemptResult result)
    {
        if (!result.CountsTowardCircuit)
        {
            return false;
        }

        var failureCount = Interlocked.Increment(ref _consecutiveFailures);
        var retryAfter = result.RetryAfter ?? TimeSpan.Zero;
        if (retryAfter > _globalOptions.BatchTimeout)
        {
            retryAfter = _globalOptions.BatchTimeout;
        }
        if (failureCount < _globalOptions.CircuitFailureThreshold && retryAfter <= TimeSpan.Zero)
        {
            return false;
        }

        var breakDuration = retryAfter > _globalOptions.CircuitBreakDuration
            ? retryAfter
            : _globalOptions.CircuitBreakDuration;
        var unavailableUntil = DateTimeOffset.UtcNow.Add(breakDuration).UtcTicks;
        var previousUnavailableUntil = Interlocked.Exchange(ref _unavailableUntilUtcTicks, unavailableUntil);
        return previousUnavailableUntil <= DateTimeOffset.UtcNow.UtcTicks;
    }

    internal void Release()
    {
        _telemetry.ChangeActiveRequests(Id, -1);
        Interlocked.Decrement(ref _active);
        _concurrency.Release();
    }

    public void Dispose()
    {
        _concurrency.Dispose();
    }
}

public sealed class AITranslationModelUnavailableException(string modelId)
    : Exception($"AI translation model '{modelId}' is temporarily unavailable.")
{
    public string ModelId { get; } = modelId;
}

public sealed class AITranslationModelLease(AITranslationModelRuntime model) : IDisposable
{
    private AITranslationModelRuntime? _model = model;

    public AITranslationModelRuntime Model => _model ?? throw new ObjectDisposedException(nameof(AITranslationModelLease));

    public void Dispose()
    {
        Interlocked.Exchange(ref _model, null)?.Release();
    }
}
