using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using SnowShot.Infrastructure.Telemetry;
using StackExchange.Redis;

namespace SnowShot.Infrastructure.Admission;

public sealed class RedisAdmissionController(
    IConnectionMultiplexer connection,
    ILogger<RedisAdmissionController>? suppliedLogger = null) : IAdmissionController
{
    private readonly ILogger<RedisAdmissionController> _logger = suppliedLogger ??
        Microsoft.Extensions.Logging.Abstractions.NullLogger<RedisAdmissionController>.Instance;
    private static readonly Action<ILogger, string, Exception?> AdmissionUnavailable =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(5101, nameof(AdmissionUnavailable)),
            "Redis admission unavailable for {Resource}");
    private static readonly Action<ILogger, string, string, Exception?> CleanupFailed =
        LoggerMessage.Define<string, string>(LogLevel.Warning, new EventId(5102, nameof(CleanupFailed)),
            "Redis admission cleanup failed for {Resource} during {Operation}");
    private const string EnqueueScript = """
        local now_parts = redis.call('TIME')
        local now = now_parts[1] * 1000 + math.floor(now_parts[2] / 1000)
        local evicted = 0
        local function remove_ticket(ticket)
          local metadata = redis.call('HGET', KEYS[6], ticket)
          redis.call('ZREM', KEYS[4], ticket); redis.call('ZREM', KEYS[5], ticket); redis.call('HDEL', KEYS[6], ticket)
          if metadata then
            local principal = string.match(metadata, '^([^|]+)|')
            local count = redis.call('HINCRBY', KEYS[7], principal, -1)
            if count <= 0 then redis.call('HDEL', KEYS[7], principal) end
          end
        end
        local expired = redis.call('ZRANGEBYSCORE', KEYS[5], '-inf', now)
        for _, ticket in ipairs(expired) do remove_ticket(ticket); evicted = evicted + 1 end
        local function refresh_active_ttl(key)
          local furthest = redis.call('ZRANGE', key, -1, -1, 'WITHSCORES')
          if furthest[2] then redis.call('PEXPIREAT', key, tonumber(furthest[2]) + 60000) end
        end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now - 60000)
        redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', now)
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[1]) then
          local oldest = redis.call('ZRANGE', KEYS[1], 0, 0, 'WITHSCORES')
          return {0, 'rate_limit', now, tonumber(oldest[2]) + 60000, evicted}
        end
        redis.call('ZADD', KEYS[1], now, ARGV[2]); redis.call('PEXPIRE', KEYS[1], 120000)
        if redis.call('ZCARD', KEYS[4]) == 0 and redis.call('ZCARD', KEYS[2]) < tonumber(ARGV[3]) and redis.call('ZCARD', KEYS[3]) < tonumber(ARGV[4]) then
          redis.call('ZADD', KEYS[2], now + tonumber(ARGV[5]), ARGV[6]); redis.call('ZADD', KEYS[3], now + tonumber(ARGV[5]), ARGV[6])
          refresh_active_ttl(KEYS[2]); refresh_active_ttl(KEYS[3])
          return {1, ARGV[6], now, now, evicted}
        end
        if redis.call('ZCARD', KEYS[4]) >= tonumber(ARGV[7]) then return {0, 'queue_full', now, now + 1000, evicted} end
        local principal_count = tonumber(redis.call('HGET', KEYS[7], ARGV[8]) or '0')
        if principal_count >= tonumber(ARGV[9]) then return {0, 'principal_queue_full', now, now + 1000, evicted} end
        local sequence = redis.call('INCR', KEYS[8])
        local deadline = now + tonumber(ARGV[11])
        redis.call('ZADD', KEYS[4], sequence, ARGV[10]); redis.call('ZADD', KEYS[5], deadline, ARGV[10])
        redis.call('HSET', KEYS[6], ARGV[10], ARGV[8] .. '|' .. deadline); redis.call('HINCRBY', KEYS[7], ARGV[8], 1)
        local furthest = redis.call('ZRANGE', KEYS[5], -1, -1, 'WITHSCORES')
        local queue_expiry = tonumber(furthest[2]) + 60000
        redis.call('PEXPIREAT', KEYS[4], queue_expiry); redis.call('PEXPIREAT', KEYS[5], queue_expiry)
        redis.call('PEXPIREAT', KEYS[6], queue_expiry); redis.call('PEXPIREAT', KEYS[7], queue_expiry)
        redis.call('PEXPIREAT', KEYS[8], queue_expiry)
        return {2, ARGV[10], now, deadline, evicted}
        """;

    private const string PollScript = """
        local now_parts = redis.call('TIME')
        local now = now_parts[1] * 1000 + math.floor(now_parts[2] / 1000)
        local evicted = 0
        local function remove_ticket(ticket)
          local metadata = redis.call('HGET', KEYS[5], ticket)
          redis.call('ZREM', KEYS[3], ticket); redis.call('ZREM', KEYS[4], ticket); redis.call('HDEL', KEYS[5], ticket)
          if metadata then
            local principal = string.match(metadata, '^([^|]+)|')
            local count = redis.call('HINCRBY', KEYS[6], principal, -1)
            if count <= 0 then redis.call('HDEL', KEYS[6], principal) end
          end
        end
        local expired = redis.call('ZRANGEBYSCORE', KEYS[4], '-inf', now)
        for _, ticket in ipairs(expired) do remove_ticket(ticket); evicted = evicted + 1 end
        redis.call('ZREMRANGEBYSCORE', KEYS[1], '-inf', now); redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', now)
        local metadata = redis.call('HGET', KEYS[5], ARGV[1])
        if not metadata then return {2, now, evicted} end
        local head = redis.call('ZRANGE', KEYS[3], 0, 0)
        if not head[1] or head[1] ~= ARGV[1] then return {0, now, evicted} end
        if redis.call('ZCARD', KEYS[1]) >= tonumber(ARGV[2]) or redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[3]) then return {0, now, evicted} end
        remove_ticket(ARGV[1])
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[4]), ARGV[5]); redis.call('ZADD', KEYS[2], now + tonumber(ARGV[4]), ARGV[5])
        local global_furthest = redis.call('ZRANGE', KEYS[1], -1, -1, 'WITHSCORES')
        local principal_furthest = redis.call('ZRANGE', KEYS[2], -1, -1, 'WITHSCORES')
        redis.call('PEXPIREAT', KEYS[1], tonumber(global_furthest[2]) + 60000)
        redis.call('PEXPIREAT', KEYS[2], tonumber(principal_furthest[2]) + 60000)
        return {1, now, evicted}
        """;

    private const string CancelScript = """
        local metadata = redis.call('HGET', KEYS[3], ARGV[1])
        redis.call('ZREM', KEYS[1], ARGV[1]); redis.call('ZREM', KEYS[2], ARGV[1]); redis.call('HDEL', KEYS[3], ARGV[1])
        if metadata then
          local principal = string.match(metadata, '^([^|]+)|')
          local count = redis.call('HINCRBY', KEYS[4], principal, -1)
          if count <= 0 then redis.call('HDEL', KEYS[4], principal) end
        end
        return 1
        """;

    private const string RenewScript = """
        local now_parts = redis.call('TIME')
        local now = now_parts[1] * 1000 + math.floor(now_parts[2] / 1000)
        local global_score = tonumber(redis.call('ZSCORE', KEYS[1], ARGV[1])); local principal_score = tonumber(redis.call('ZSCORE', KEYS[2], ARGV[1]))
        if global_score == nil or principal_score == nil or global_score <= now or principal_score <= now then
          redis.call('ZREM', KEYS[1], ARGV[1]); redis.call('ZREM', KEYS[2], ARGV[1]); return 0
        end
        redis.call('ZADD', KEYS[1], now + tonumber(ARGV[2]), ARGV[1]); redis.call('ZADD', KEYS[2], now + tonumber(ARGV[2]), ARGV[1])
        local global_furthest = redis.call('ZRANGE', KEYS[1], -1, -1, 'WITHSCORES')
        local principal_furthest = redis.call('ZRANGE', KEYS[2], -1, -1, 'WITHSCORES')
        redis.call('PEXPIREAT', KEYS[1], tonumber(global_furthest[2]) + 60000)
        redis.call('PEXPIREAT', KEYS[2], tonumber(principal_furthest[2]) + 60000); return 1
        """;

    private const string ReleaseScript = "redis.call('ZREM', KEYS[1], ARGV[1]); redis.call('ZREM', KEYS[2], ARGV[1]); return 1";

    public async Task<IAdmissionLease> AcquireAsync(AdmissionRequest request, CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("admission.acquire");
        try
        {
            var database = connection.GetDatabase();
            var tag = $"{{snowshot:{request.Resource}}}";
            var principal = Hash(request.PrincipalKey);
            RedisKey rate = $"{tag}:rate:{principal}";
            RedisKey global = $"{tag}:active";
            RedisKey own = $"{tag}:active:{principal}";
            RedisKey queue = $"{tag}:queue:order";
            RedisKey queueExpiry = $"{tag}:queue:expiry";
            RedisKey queueMetadata = $"{tag}:queue:metadata";
            RedisKey queueCounts = $"{tag}:queue:principal-counts";
            RedisKey queueSequence = $"{tag}:queue:sequence";
            var owner = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var rateMember = $"{owner}:{Guid.NewGuid():N}";
            var result = (RedisResult[]?)(await database.ScriptEvaluateAsync(EnqueueScript,
                [rate, global, own, queue, queueExpiry, queueMetadata, queueCounts, queueSequence],
                [request.Policy.RequestsPerMinute, rateMember, request.Policy.GlobalConcurrency,
                    request.Policy.PerPrincipalConcurrency, Milliseconds(request.LeaseTtl), owner,
                    request.Policy.GlobalQueueLength, principal, request.Policy.PerPrincipalQueueLength,
                    ticket, Milliseconds(request.QueueWait)]).WaitAsync(cancellationToken))
                ?? throw new RedisException("Admission script returned no result.");
            var status = (int)result[0];
            RecordEvictions(request.Resource, result, 4);
            if (status == 0)
            {
                var reason = (string)result[1]!;
                if (string.Equals(reason, "principal_queue_full", StringComparison.Ordinal))
                    SnowShotTelemetry.PrincipalQueueRejections.Add(1,
                        new KeyValuePair<string, object?>[] { new("resource", request.Resource) });
                return RedisLease.Rejected(ParseReason(reason), RetryAfter(result));
            }
            if (status == 1)
            {
                RecordActive(request.Resource, 1);
                return new RedisLease(database, global, own, owner, AdmissionRejectionReason.None, request, _logger);
            }

            var started = Stopwatch.GetTimestamp();
            try
            {
                while (Stopwatch.GetElapsedTime(started) <= request.QueueWait + TimeSpan.FromMilliseconds(100))
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
                    result = (RedisResult[]?)(await database.ScriptEvaluateAsync(PollScript,
                        [global, own, queue, queueExpiry, queueMetadata, queueCounts],
                        [ticket, request.Policy.GlobalConcurrency, request.Policy.PerPrincipalConcurrency,
                            Milliseconds(request.LeaseTtl), owner]).WaitAsync(cancellationToken))
                        ?? throw new RedisException("Admission poll returned no result.");
                    status = (int)result[0];
                    RecordEvictions(request.Resource, result, 2);
                    if (status == 1)
                    {
                        SnowShotTelemetry.QueueWaitMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                        RecordActive(request.Resource, 1);
                        return new RedisLease(database, global, own, owner, AdmissionRejectionReason.None, request, _logger);
                    }
                    if (status == 2) break;
                }
            }
            catch (OperationCanceledException)
            {
                await CancelTicketAsync(database, queue, queueExpiry, queueMetadata, queueCounts, ticket, request.Resource, _logger);
                throw;
            }
            await CancelTicketAsync(database, queue, queueExpiry, queueMetadata, queueCounts, ticket, request.Resource, _logger);
            return RedisLease.Rejected(AdmissionRejectionReason.QueueTimeout, TimeSpan.FromSeconds(1));
        }
        catch (RedisException exception)
        {
            SnowShotTelemetry.AdmissionDependencyFailures.Add(1, Tags(request.Resource));
            AdmissionUnavailable(_logger, request.Resource, exception);
            return RedisLease.Rejected(AdmissionRejectionReason.DependencyUnavailable, TimeSpan.FromSeconds(1));
        }
    }

    public async Task<bool> CheckReadyAsync(CancellationToken cancellationToken)
    {
        try { await connection.GetDatabase().PingAsync().WaitAsync(TimeSpan.FromSeconds(3), cancellationToken); return true; }
        catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException) { return false; }
    }

    private static async Task CancelTicketAsync(
        IDatabase database,
        RedisKey queue,
        RedisKey expiry,
        RedisKey metadata,
        RedisKey counts,
        RedisValue ticket,
        string resource,
        ILogger logger)
    {
        try { await database.ScriptEvaluateAsync(CancelScript, [queue, expiry, metadata, counts], [ticket]).WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (Exception exception) when (exception is RedisException or TimeoutException)
        {
            SnowShotTelemetry.AdmissionCleanupFailures.Add(1, Tags(resource, "cancel_ticket"));
            CleanupFailed(logger, resource, "cancel_ticket", exception);
        }
    }

    private static long Milliseconds(TimeSpan value) => Math.Max(1, checked((long)Math.Ceiling(value.TotalMilliseconds)));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static TimeSpan RetryAfter(RedisResult[] result) => TimeSpan.FromMilliseconds(Math.Max(1, (long)result[3] - (long)result[2]));
    private static void RecordEvictions(string resource, RedisResult[] result, int index)
    {
        var count = (long)result[index];
        if (count > 0) SnowShotTelemetry.StaleQueueTickets.Add(count,
            new KeyValuePair<string, object?>[] { new("resource", resource) });
    }
    private static AdmissionRejectionReason ParseReason(string reason) => reason switch
    {
        "rate_limit" => AdmissionRejectionReason.RateLimit,
        "queue_full" or "principal_queue_full" => AdmissionRejectionReason.QueueFull,
        _ => AdmissionRejectionReason.DependencyUnavailable,
    };

    private static KeyValuePair<string, object?>[] Tags(string resource, string? operation = null) => operation is null
        ? [new("resource", resource), new("backend", "redis")]
        : [new("resource", resource), new("backend", "redis"), new("operation", operation)];

    private static void RecordActive(string resource, long delta) =>
        SnowShotTelemetry.ActiveLeases.Add(delta, Tags(resource));

    private sealed class RedisLease(
        IDatabase? database, RedisKey global, RedisKey principal, string ownerToken,
        AdmissionRejectionReason rejectionReason, AdmissionRequest? request, ILogger logger) : IAdmissionLease
    {
        private readonly CancellationTokenSource _lost = new();
        private int _released;
        private int _active = database is null ? 0 : 1;
        public bool Acquired => database is not null;
        public TimeSpan? RetryAfter { get; private init; }
        public AdmissionRejectionReason RejectionReason { get; } = rejectionReason;
        public CancellationToken OwnershipLost => _lost.Token;
        public static RedisLease Rejected(AdmissionRejectionReason reason, TimeSpan retryAfter) =>
            new(null, default, default, string.Empty, reason, null,
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)
            { RetryAfter = retryAfter };

        public async Task<bool> RenewAsync(CancellationToken cancellationToken)
        {
            if (database is null || request is null || Volatile.Read(ref _released) != 0) return false;
            try
            {
                var result = (int)await database.ScriptEvaluateAsync(RenewScript, [global, principal],
                    [ownerToken, Milliseconds(request.LeaseTtl)]).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
                if (result == 1)
                {
                    SnowShotTelemetry.RenewedLeases.Add(1, Tags(request.Resource, "admission"));
                    return true;
                }
            }
            catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException) { }
            await _lost.CancelAsync();
            FinishActive(lost: true);
            return false;
        }

        public async Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (database is null || Interlocked.Exchange(ref _released, 1) != 0) return;
            try { await database.ScriptEvaluateAsync(ReleaseScript, [global, principal], [ownerToken]).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken); }
            catch (Exception exception) when (exception is RedisException or TimeoutException or OperationCanceledException)
            {
                var resource = request?.Resource ?? "unknown";
                SnowShotTelemetry.AdmissionCleanupFailures.Add(1, Tags(resource, "release"));
                CleanupFailed(logger, resource, "release", exception);
            }
            finally { FinishActive(lost: false); }
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReleaseAsync(timeout.Token);
            _lost.Dispose();
        }

        private void FinishActive(bool lost)
        {
            if (request is null || Interlocked.Exchange(ref _active, 0) == 0) return;
            RecordActive(request.Resource, -1);
            if (lost) SnowShotTelemetry.LostLeases.Add(1, Tags(request.Resource, "admission"));
        }
    }
}
