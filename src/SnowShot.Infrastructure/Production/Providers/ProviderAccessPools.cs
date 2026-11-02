using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Telemetry;
using StackExchange.Redis;

namespace SnowShot.Infrastructure.Providers;

public sealed class InMemoryProviderAccessPool(
    ProviderModelCatalog catalog,
    IProviderCircuitRegistry circuits) : IProviderAccessPool
{
    private readonly ConcurrentDictionary<string, ModelState> _models = new(StringComparer.Ordinal);

    public async Task<IProviderAccessLease> AcquireAsync(ProviderAccessRequest request, CancellationToken cancellationToken)
    {
        var selections = catalog.Selections(request.LogicalModel);
        var exclude = request.ExcludedAccessIds.Count < selections.Count
            ? new HashSet<string>(request.ExcludedAccessIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var state = _models.GetOrAdd(request.LogicalModel, _ => new(selections,
            selections.ToDictionary(
                selection => selection.AccessId,
                selection => catalog.GetMaxConcurrentRequests(selection),
                StringComparer.Ordinal)));
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            ProviderAccessSelection? candidate = null;
            lock (state.Gate)
            {
                for (var offset = 1; offset <= state.Selections.Count; offset++)
                {
                    var index = (state.Cursor + offset) % state.Selections.Count;
                    var selection = state.Selections[index];
                    if (exclude.Contains(selection.AccessId) ||
                        state.Active[selection.AccessId] >= state.MaxConcurrentRequests[selection.AccessId]) continue;
                    state.Cursor = index;
                    state.Active[selection.AccessId]++;
                    candidate = selection;
                    break;
                }
            }
            if (candidate is not null)
            {
                if (await circuits.TryAcquireAsync(candidate, cancellationToken))
                {
                    RecordSelection(candidate);
                    return new MemoryLease(candidate, state);
                }
                SnowShotTelemetry.CircuitOpen.Add(1, Tags(candidate));
                lock (state.Gate) state.Active[candidate.AccessId]--;
                exclude.Add(candidate.AccessId);
                if (exclude.Count == selections.Count) return RejectedLease.Unavailable();
                continue;
            }
            if (Stopwatch.GetElapsedTime(started) >= request.QueueWait)
                return RejectedLease.Saturated();
            var remaining = request.QueueWait - Stopwatch.GetElapsedTime(started);
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(25) ? remaining : TimeSpan.FromMilliseconds(25), cancellationToken);
        }
    }

    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private static void RecordSelection(ProviderAccessSelection selection)
    {
        SnowShotTelemetry.ProviderAccessSelections.Add(1, Tags(selection));
        SnowShotTelemetry.ProviderAccessActive.Add(1, Tags(selection));
    }

    private sealed class ModelState(
        IReadOnlyList<ProviderAccessSelection> selections,
        IReadOnlyDictionary<string, int> maxConcurrentRequests)
    {
        public object Gate { get; } = new();
        public IReadOnlyList<ProviderAccessSelection> Selections { get; } = selections;
        public IReadOnlyDictionary<string, int> MaxConcurrentRequests { get; } = maxConcurrentRequests;
        public Dictionary<string, int> Active { get; } = selections.ToDictionary(value => value.AccessId, _ => 0, StringComparer.Ordinal);
        public int Cursor { get; set; } = selections.Count - 1;
    }

    private sealed class MemoryLease : IProviderAccessLease
    {
        private readonly ModelState _state;
        private int _released;
        public MemoryLease(ProviderAccessSelection selection, ModelState state)
        {
            Selection = selection;
            _state = state;
        }
        public bool Acquired => true;
        public ProviderAccessSelection Selection { get; }
        ProviderAccessSelection? IProviderAccessLease.Selection => Selection;
        public ProviderAccessRejectionReason RejectionReason => ProviderAccessRejectionReason.None;
        public TimeSpan? RetryAfter => null;
        public CancellationToken OwnershipLost => CancellationToken.None;

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return Task.CompletedTask;
            lock (_state.Gate) _state.Active[Selection.AccessId]--;
            SnowShotTelemetry.ProviderAccessActive.Add(-1, Tags(Selection));
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync() => await ReleaseAsync(CancellationToken.None);
    }

    private static KeyValuePair<string, object?>[] Tags(ProviderAccessSelection selection) =>
        [new("model", selection.LogicalModel), new("provider", selection.Provider), new("access", selection.AccessId)];
}

public sealed class RedisProviderAccessPool(
    ProviderModelCatalog catalog,
    IConnectionMultiplexer connection,
    IProviderCircuitRegistry circuits,
    ILogger<RedisProviderAccessPool> logger) : IProviderAccessPool
{
    private static readonly Action<ILogger, string, Exception?> CoordinationUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5501, nameof(CoordinationUnavailable)),
            "Provider access coordination unavailable for model {Model}");
    private static readonly Action<ILogger, string, string, Exception?> LeaseOperationFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5502, nameof(LeaseOperationFailed)),
            "Provider access lease operation {Operation} failed for {Access}");
    private const string AcquireScript = """
        local now_parts = redis.call('TIME')
        local now = now_parts[1] * 1000 + math.floor(now_parts[2] / 1000)
        local count = tonumber(ARGV[3])
        for index = 1, count do redis.call('ZREMRANGEBYSCORE', KEYS[index + 1], '-inf', now) end
        local last = redis.call('GET', KEYS[1])
        local start = 0
        for index = 1, count do if ARGV[3 + index] == last then start = index end end
        for offset = 1, count do
          local index = ((start + offset - 1) % count) + 1
          if ARGV[3 + 2 * count + index] == '0' and redis.call('ZCARD', KEYS[index + 1]) < tonumber(ARGV[3 + count + index]) then
            local expiry = now + tonumber(ARGV[1])
            redis.call('ZADD', KEYS[index + 1], expiry, ARGV[2])
            redis.call('PEXPIREAT', KEYS[index + 1], expiry + 60000)
            redis.call('SET', KEYS[1], ARGV[3 + index], 'PX', 86400000)
            return {1, index}
          end
        end
        return {0, 0}
        """;

    private const string RenewScript = """
        local now_parts = redis.call('TIME')
        local now = now_parts[1] * 1000 + math.floor(now_parts[2] / 1000)
        local score = tonumber(redis.call('ZSCORE', KEYS[1], ARGV[1]))
        if score == nil or score <= now then redis.call('ZREM', KEYS[1], ARGV[1]); return 0 end
        local expiry = now + tonumber(ARGV[2])
        redis.call('ZADD', KEYS[1], expiry, ARGV[1]); redis.call('PEXPIREAT', KEYS[1], expiry + 60000)
        return 1
        """;

    private const string ReleaseScript = "return redis.call('ZREM', KEYS[1], ARGV[1])";

    public async Task<IProviderAccessLease> AcquireAsync(ProviderAccessRequest request, CancellationToken cancellationToken)
    {
        var selections = catalog.Selections(request.LogicalModel);
        var exclude = request.ExcludedAccessIds.Count < selections.Count
            ? new HashSet<string>(request.ExcludedAccessIds, StringComparer.Ordinal)
            : new HashSet<string>(StringComparer.Ordinal);
        var database = connection.GetDatabase();
        var tag = $"{{snowshot:provider:{Hash(request.LogicalModel)}}}";
        RedisKey cursor = $"{tag}:cursor";
        var active = selections.Select(value => (RedisKey)$"{tag}:active:{Hash(value.AccessId)}").ToArray();
        var keys = new[] { cursor }.Concat(active).ToArray();
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (true)
            {
                var arguments = new List<RedisValue>
                {
                    Milliseconds(request.LeaseTtl), token, selections.Count,
                };
                arguments.AddRange(selections.Select(value => (RedisValue)value.AccessId));
                arguments.AddRange(selections.Select(value => (RedisValue)catalog.GetMaxConcurrentRequests(value)));
                arguments.AddRange(selections.Select(value => (RedisValue)(exclude.Contains(value.AccessId) ? "1" : "0")));
                var result = (RedisResult[]?)(await database.ScriptEvaluateAsync(AcquireScript, keys, arguments.ToArray())
                    .WaitAsync(cancellationToken)) ?? throw new RedisException("Provider access selection returned no result.");
                if ((int)result[0] == 1)
                {
                    var index = (int)result[1] - 1;
                    var selection = selections[index];
                    if (!await circuits.TryAcquireAsync(selection, cancellationToken))
                    {
                        SnowShotTelemetry.CircuitOpen.Add(1, Tags(selection));
                        await database.SortedSetRemoveAsync(active[index], token).WaitAsync(cancellationToken);
                        exclude.Add(selection.AccessId);
                        if (exclude.Count == selections.Count) return RejectedLease.Unavailable();
                        continue;
                    }
                    RecordSelection(selection);
                    return new RedisLease(database, active[index], token, selection, request, logger);
                }
                if (Stopwatch.GetElapsedTime(started) >= request.QueueWait)
                {
                    SnowShotTelemetry.ProviderAccessSaturation.Add(1,
                        new KeyValuePair<string, object?>[] { new("model", request.LogicalModel) });
                    return RejectedLease.Saturated();
                }
                var remaining = request.QueueWait - Stopwatch.GetElapsedTime(started);
                await Task.Delay(remaining < TimeSpan.FromMilliseconds(50) ? remaining : TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            CoordinationUnavailable(logger, request.LogicalModel, exception);
            SnowShotTelemetry.ProviderAccessDependencyFailures.Add(1,
                new KeyValuePair<string, object?>[] { new("model", request.LogicalModel) });
            return RejectedLease.Unavailable();
        }
    }

    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
    {
        try { await connection.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); return true; }
        catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException) { return false; }
    }

    private static void RecordSelection(ProviderAccessSelection selection)
    {
        SnowShotTelemetry.ProviderAccessSelections.Add(1, Tags(selection));
        SnowShotTelemetry.ProviderAccessActive.Add(1, Tags(selection));
    }

    private static long Milliseconds(TimeSpan value) => Math.Max(1, checked((long)Math.Ceiling(value.TotalMilliseconds)));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static KeyValuePair<string, object?>[] Tags(ProviderAccessSelection selection) =>
        [new("model", selection.LogicalModel), new("provider", selection.Provider), new("access", selection.AccessId)];

    private sealed class RedisLease : IProviderAccessLease
    {
        private readonly IDatabase _database;
        private readonly RedisKey _active;
        private readonly string _token;
        private readonly ProviderAccessRequest _request;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _lost = new();
        private readonly CancellationTokenSource _renewalStop = new();
        private readonly Task _renewal;
        private int _released;

        public RedisLease(IDatabase database, RedisKey active, string token, ProviderAccessSelection selection,
            ProviderAccessRequest request, ILogger logger)
        {
            _database = database;
            _active = active;
            _token = token;
            Selection = selection;
            _request = request;
            _logger = logger;
            _renewal = RenewLoopAsync(_renewalStop.Token);
        }

        public bool Acquired => true;
        public ProviderAccessSelection Selection { get; }
        ProviderAccessSelection? IProviderAccessLease.Selection => Selection;
        public ProviderAccessRejectionReason RejectionReason => ProviderAccessRejectionReason.None;
        public TimeSpan? RetryAfter => null;
        public CancellationToken OwnershipLost => _lost.Token;

        public async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0) return;
            await _renewalStop.CancelAsync();
            try { await _renewal; } catch (OperationCanceledException) { }
            try { await _database.ScriptEvaluateAsync(ReleaseScript, [_active], [_token]).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException)
            {
                LeaseOperationFailed(_logger, "release", $"{Selection.LogicalModel}/{Selection.AccessId}", exception);
            }
            SnowShotTelemetry.ProviderAccessActive.Add(-1, Tags(Selection));
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReleaseAsync(timeout.Token);
            _renewalStop.Dispose();
            _lost.Dispose();
        }

        private async Task RenewLoopAsync(CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(_request.RenewalInterval);
            try
            {
                while (await timer.WaitForNextTickAsync(cancellationToken))
                {
                    var renewed = (int)await _database.ScriptEvaluateAsync(RenewScript, [_active],
                        [_token, Milliseconds(_request.LeaseTtl)]).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                    if (renewed == 1) continue;
                    await _lost.CancelAsync();
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception exception) when (exception is RedisException or TimeoutException)
            {
                LeaseOperationFailed(_logger, "renew", $"{Selection.LogicalModel}/{Selection.AccessId}", exception);
                await _lost.CancelAsync();
            }
        }
    }
}

internal sealed class RejectedLease : IProviderAccessLease
{
    private RejectedLease(ProviderAccessRejectionReason reason) => RejectionReason = reason;
    public bool Acquired => false;
    public ProviderAccessSelection? Selection => null;
    public ProviderAccessRejectionReason RejectionReason { get; }
    public TimeSpan? RetryAfter => TimeSpan.FromSeconds(1);
    public CancellationToken OwnershipLost => CancellationToken.None;
    public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public static RejectedLease Saturated() => new(ProviderAccessRejectionReason.Saturated);
    public static RejectedLease Unavailable() => new(ProviderAccessRejectionReason.DependencyUnavailable);
}
