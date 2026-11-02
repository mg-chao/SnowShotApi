using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using SnowShot.Application;
using SnowShot.Infrastructure.Configuration;
using StackExchange.Redis;

namespace SnowShot.Infrastructure.Providers;

public enum ProviderCircuitState { Closed, Open, HalfOpen }
public enum ProviderCircuitOutcome { Success, TransientFailure, AuthenticationFailure }

public sealed record ProviderCircuitSnapshot(
    ProviderAccessSelection Selection,
    ProviderCircuitState State,
    DateTimeOffset? OpenUntil,
    int ConsecutiveFailures,
    int BackoffLevel,
    int HalfOpenSuccesses);

public interface IProviderCircuitRegistry
{
    ValueTask<bool> TryAcquireAsync(ProviderAccessSelection selection, CancellationToken cancellationToken);
    ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome,
        TimeSpan? retryAfter, CancellationToken cancellationToken);
    ValueTask<IReadOnlyList<ProviderCircuitSnapshot>> SnapshotAsync(CancellationToken cancellationToken);
    ValueTask InitializeAsync(IEnumerable<ProviderAccessSelection> selections, CancellationToken cancellationToken);
}

public sealed class ProviderCircuitInitializationService(
    IProviderCircuitRegistry circuits,
    ProviderModelCatalog catalog) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        circuits.InitializeAsync(catalog.All.Select(value => value.Selection), cancellationToken).AsTask();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class InMemoryProviderCircuitRegistry(
    ProviderCircuitOptions options,
    TimeProvider timeProvider) : IProviderCircuitRegistry
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public ValueTask<bool> TryAcquireAsync(ProviderAccessSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = EntryFor(selection);
        lock (entry.Gate)
        {
            var now = timeProvider.GetUtcNow();
            if (entry.State == ProviderCircuitState.Closed) return ValueTask.FromResult(true);
            if (entry.State == ProviderCircuitState.Open)
            {
                if (entry.OpenUntil > now) return ValueTask.FromResult(false);
                entry.State = ProviderCircuitState.HalfOpen;
                entry.HalfOpenSuccesses = 0;
                entry.ProbeUntil = now.AddSeconds(options.ProbeLeaseSeconds);
                return ValueTask.FromResult(true);
            }
            if (entry.ProbeUntil > now) return ValueTask.FromResult(false);
            entry.ProbeUntil = now.AddSeconds(options.ProbeLeaseSeconds);
            return ValueTask.FromResult(true);
        }
    }

    public ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome,
        TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = EntryFor(selection);
        lock (entry.Gate)
        {
            var now = timeProvider.GetUtcNow();
            Trim(entry, now);
            if (outcome == ProviderCircuitOutcome.AuthenticationFailure)
            {
                Open(entry, now, TimeSpan.FromMinutes(10));
                return ValueTask.CompletedTask;
            }

            entry.Window.Enqueue((now, outcome == ProviderCircuitOutcome.TransientFailure));
            if (outcome == ProviderCircuitOutcome.Success)
            {
                entry.ConsecutiveFailures = 0;
                if (entry.State == ProviderCircuitState.HalfOpen)
                {
                    entry.HalfOpenSuccesses++;
                    entry.ProbeUntil = DateTimeOffset.MinValue;
                    if (entry.HalfOpenSuccesses >= options.HalfOpenSuccessesToClose) Close(entry, now);
                }
                else if (entry.State == ProviderCircuitState.Closed &&
                         entry.ClosedSince != DateTimeOffset.MinValue &&
                         now - entry.ClosedSince >= TimeSpan.FromSeconds(options.StableResetSeconds))
                {
                    entry.BackoffLevel = 0;
                }
                return ValueTask.CompletedTask;
            }

            entry.ConsecutiveFailures++;
            if (entry.State == ProviderCircuitState.HalfOpen)
            {
                Open(entry, now, retryAfter);
                return ValueTask.CompletedTask;
            }
            if (entry.State == ProviderCircuitState.Open) return ValueTask.CompletedTask;
            var failures = entry.Window.Count(value => value.Failure);
            if (entry.ConsecutiveFailures >= options.ConsecutiveFailuresToOpen ||
                entry.Window.Count >= options.MinimumThroughput &&
                (double)failures / entry.Window.Count >= options.FailureRatio)
                Open(entry, now, retryAfter);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask<IReadOnlyList<ProviderCircuitSnapshot>> SnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = _entries.Values.Select(entry =>
        {
            lock (entry.Gate)
            {
                return new ProviderCircuitSnapshot(entry.Selection, entry.State,
                    entry.State == ProviderCircuitState.Open ? entry.OpenUntil : null,
                    entry.ConsecutiveFailures, entry.BackoffLevel, entry.HalfOpenSuccesses);
            }
        }).ToArray();
        return ValueTask.FromResult<IReadOnlyList<ProviderCircuitSnapshot>>(snapshots);
    }

    public ValueTask InitializeAsync(IEnumerable<ProviderAccessSelection> selections, CancellationToken cancellationToken)
    {
        foreach (var selection in selections)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = EntryFor(selection);
            if (!options.InitiallyOpenAccesses.Contains(Identity(selection), StringComparer.Ordinal)) continue;
            lock (entry.Gate)
            {
                if (entry.Initialized) continue;
                entry.State = ProviderCircuitState.Open;
                entry.OpenUntil = timeProvider.GetUtcNow();
                entry.Initialized = true;
            }
        }
        return ValueTask.CompletedTask;
    }

    private Entry EntryFor(ProviderAccessSelection selection) =>
        _entries.GetOrAdd(Identity(selection), _ => new(selection, timeProvider.GetUtcNow()));

    private void Trim(Entry entry, DateTimeOffset now)
    {
        var cutoff = now.AddSeconds(-options.SamplingSeconds);
        while (entry.Window.TryPeek(out var value) && value.At < cutoff) entry.Window.Dequeue();
    }

    private void Open(Entry entry, DateTimeOffset now, TimeSpan? retryAfter)
    {
        entry.BackoffLevel = Math.Min(entry.BackoffLevel + 1, options.MaximumBackoffLevel);
        var seconds = Math.Min(options.MaximumBreakSeconds,
            options.InitialBreakSeconds * Math.Pow(2, entry.BackoffLevel - 1));
        var duration = TimeSpan.FromSeconds(seconds);
        if (retryAfter > duration) duration = retryAfter.Value;
        entry.State = ProviderCircuitState.Open;
        entry.OpenUntil = now.Add(duration);
        entry.ProbeUntil = DateTimeOffset.MinValue;
        entry.HalfOpenSuccesses = 0;
    }

    private static void Close(Entry entry, DateTimeOffset now)
    {
        entry.State = ProviderCircuitState.Closed;
        entry.ClosedSince = now;
        entry.ProbeUntil = DateTimeOffset.MinValue;
        entry.HalfOpenSuccesses = 0;
        entry.ConsecutiveFailures = 0;
        entry.Window.Clear();
    }

    internal static string Identity(ProviderAccessSelection selection) =>
        $"{selection.LogicalModel}/{selection.Provider}/{selection.AccessId}";

    private sealed class Entry(ProviderAccessSelection selection, DateTimeOffset createdAt)
    {
        public object Gate { get; } = new();
        public ProviderAccessSelection Selection { get; } = selection;
        public Queue<(DateTimeOffset At, bool Failure)> Window { get; } = new();
        public ProviderCircuitState State { get; set; }
        public DateTimeOffset OpenUntil { get; set; }
        public DateTimeOffset ProbeUntil { get; set; }
        public DateTimeOffset ClosedSince { get; set; } = createdAt;
        public int ConsecutiveFailures { get; set; }
        public int BackoffLevel { get; set; }
        public int HalfOpenSuccesses { get; set; }
        public bool Initialized { get; set; }
    }
}

public sealed class RedisProviderCircuitRegistry(
    IConnectionMultiplexer connection,
    ProviderCircuitOptions options) : IProviderCircuitRegistry
{
    private readonly ConcurrentDictionary<string, ProviderAccessSelection> _selections = new(StringComparer.Ordinal);

    private const string TryAcquireScript = """
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local state = redis.call('HGET', KEYS[1], 'state') or 'closed'
        if state == 'closed' then redis.call('EXPIRE', KEYS[1], ARGV[2]); return 1 end
        if state == 'open' then
          local open_until = tonumber(redis.call('HGET', KEYS[1], 'open_until') or '0')
          if open_until > now then return 0 end
          redis.call('HSET', KEYS[1], 'state', 'half_open', 'half_successes', 0, 'probe_until', now + tonumber(ARGV[1]))
          redis.call('EXPIRE', KEYS[1], ARGV[2])
          return 1
        end
        local probe_until = tonumber(redis.call('HGET', KEYS[1], 'probe_until') or '0')
        if probe_until > now then return 0 end
        redis.call('HSET', KEYS[1], 'probe_until', now + tonumber(ARGV[1]))
        redis.call('EXPIRE', KEYS[1], ARGV[2])
        return 1
        """;

    private const string InitializeScript = """
        if redis.call('EXISTS', KEYS[1]) == 1 then return 0 end
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        redis.call('HSET', KEYS[1], 'state', 'open', 'open_until', now, 'level', 0,
          'consecutive', 0, 'half_successes', 0, 'probe_until', 0)
        redis.call('EXPIRE', KEYS[1], ARGV[1])
        return 1
        """;

    private const string ReportScript = """
        local time = redis.call('TIME')
        local now = time[1] * 1000 + math.floor(time[2] / 1000)
        local state = redis.call('HGET', KEYS[1], 'state') or 'closed'
        local level = tonumber(redis.call('HGET', KEYS[1], 'level') or '0')
        local function open_circuit(minimum)
          level = math.min(level + 1, tonumber(ARGV[8]))
          local duration = math.min(tonumber(ARGV[7]), tonumber(ARGV[6]) * (2 ^ (level - 1)))
          if tonumber(minimum) > duration then duration = tonumber(minimum) end
          redis.call('HSET', KEYS[1], 'state', 'open', 'open_until', now + duration, 'level', level,
            'half_successes', 0, 'probe_until', 0)
        end
        if ARGV[1] == 'auth' then
          open_circuit(math.max(600000, tonumber(ARGV[2])))
        else
          redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now - tonumber(ARGV[3]))
          redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now - tonumber(ARGV[3]))
          redis.call('ZADD', KEYS[2], now, ARGV[9])
          if ARGV[1] == 'failure' then redis.call('ZADD', KEYS[3], now, ARGV[9]) end
          if ARGV[1] == 'success' then
            redis.call('HSET', KEYS[1], 'consecutive', 0)
            if state == 'half_open' then
              local successes = tonumber(redis.call('HGET', KEYS[1], 'half_successes') or '0') + 1
              if successes >= tonumber(ARGV[10]) then
                redis.call('HSET', KEYS[1], 'state', 'closed', 'closed_since', now, 'half_successes', 0,
                  'probe_until', 0, 'consecutive', 0)
                redis.call('DEL', KEYS[2], KEYS[3])
              else
                redis.call('HSET', KEYS[1], 'half_successes', successes, 'probe_until', 0)
              end
            elseif state == 'closed' then
              local closed_since = tonumber(redis.call('HGET', KEYS[1], 'closed_since') or tostring(now))
              if now - closed_since >= tonumber(ARGV[11]) then redis.call('HSET', KEYS[1], 'level', 0) end
            end
          else
            local consecutive = tonumber(redis.call('HGET', KEYS[1], 'consecutive') or '0') + 1
            redis.call('HSET', KEYS[1], 'consecutive', consecutive)
            if state == 'half_open' then
              open_circuit(tonumber(ARGV[2]))
            elseif state == 'closed' then
              local total = redis.call('ZCARD', KEYS[2])
              local failures = redis.call('ZCARD', KEYS[3])
              if consecutive >= tonumber(ARGV[4]) or
                 (total >= tonumber(ARGV[5]) and failures / total >= tonumber(ARGV[12])) then
                open_circuit(tonumber(ARGV[2]))
              end
            end
          end
        end
        redis.call('EXPIRE', KEYS[1], ARGV[13]); redis.call('EXPIRE', KEYS[2], ARGV[13]); redis.call('EXPIRE', KEYS[3], ARGV[13])
        return redis.call('HGET', KEYS[1], 'state') or 'closed'
        """;

    public async ValueTask<bool> TryAcquireAsync(ProviderAccessSelection selection, CancellationToken cancellationToken)
    {
        Remember(selection);
        var result = await connection.GetDatabase().ScriptEvaluateAsync(TryAcquireScript,
            [StateKey(selection)], [Milliseconds(TimeSpan.FromSeconds(options.ProbeLeaseSeconds)), options.StateTtlSeconds])
            .WaitAsync(cancellationToken);
        return (int)result == 1;
    }

    public async ValueTask ReportAsync(ProviderAccessSelection selection, ProviderCircuitOutcome outcome,
        TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        Remember(selection);
        var identity = Guid.NewGuid().ToString("N");
        RedisValue[] arguments =
        [
            outcome switch
            {
                ProviderCircuitOutcome.Success => "success",
                ProviderCircuitOutcome.AuthenticationFailure => "auth",
                _ => "failure",
            },
            Milliseconds(retryAfter ?? TimeSpan.Zero),
            options.SamplingSeconds * 1000,
            options.ConsecutiveFailuresToOpen,
            options.MinimumThroughput,
            options.InitialBreakSeconds * 1000,
            options.MaximumBreakSeconds * 1000,
            options.MaximumBackoffLevel,
            identity,
            options.HalfOpenSuccessesToClose,
            options.StableResetSeconds * 1000,
            options.FailureRatio,
            options.StateTtlSeconds,
        ];
        await connection.GetDatabase().ScriptEvaluateAsync(ReportScript,
            [StateKey(selection), TotalKey(selection), FailureKey(selection)], arguments).WaitAsync(cancellationToken);
    }

    public async ValueTask<IReadOnlyList<ProviderCircuitSnapshot>> SnapshotAsync(CancellationToken cancellationToken)
    {
        var database = connection.GetDatabase();
        var values = new List<ProviderCircuitSnapshot>();
        foreach (var selection in _selections.Values)
        {
            var fields = await database.HashGetAllAsync(StateKey(selection)).WaitAsync(cancellationToken);
            var map = fields.ToDictionary(value => value.Name.ToString(), value => value.Value.ToString(), StringComparer.Ordinal);
            var state = map.GetValueOrDefault("state") switch
            {
                "open" => ProviderCircuitState.Open,
                "half_open" => ProviderCircuitState.HalfOpen,
                _ => ProviderCircuitState.Closed,
            };
            DateTimeOffset? openUntil = map.TryGetValue("open_until", out var raw) && long.TryParse(raw, out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
                : null;
            values.Add(new(selection, state, openUntil, Integer(map, "consecutive"), Integer(map, "level"),
                Integer(map, "half_successes")));
        }
        return values;
    }

    public async ValueTask InitializeAsync(IEnumerable<ProviderAccessSelection> selections, CancellationToken cancellationToken)
    {
        var database = connection.GetDatabase();
        foreach (var selection in selections)
        {
            Remember(selection);
            if (!options.InitiallyOpenAccesses.Contains(InMemoryProviderCircuitRegistry.Identity(selection), StringComparer.Ordinal)) continue;
            await database.ScriptEvaluateAsync(InitializeScript, [StateKey(selection)], [options.StateTtlSeconds])
                .WaitAsync(cancellationToken);
        }
    }

    private void Remember(ProviderAccessSelection selection) =>
        _selections[InMemoryProviderCircuitRegistry.Identity(selection)] = selection;

    private static int Integer(Dictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var raw) && int.TryParse(raw, out var parsed) ? parsed : 0;
    private static long Milliseconds(TimeSpan value) => Math.Max(0, checked((long)Math.Ceiling(value.TotalMilliseconds)));
    private static RedisKey StateKey(ProviderAccessSelection selection) => Keys(selection).State;
    private static RedisKey TotalKey(ProviderAccessSelection selection) => Keys(selection).Total;
    private static RedisKey FailureKey(ProviderAccessSelection selection) => Keys(selection).Failures;
    internal static (string State, string Total, string Failures) Keys(ProviderAccessSelection selection)
    {
        var tag = $"{{{Hash(selection)}}}";
        return ($"snowshot:provider:circuit:{tag}:state", $"snowshot:provider:circuit:{tag}:total",
            $"snowshot:provider:circuit:{tag}:failures");
    }
    private static string Hash(ProviderAccessSelection selection) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(InMemoryProviderCircuitRegistry.Identity(selection)))).ToLowerInvariant();
}
