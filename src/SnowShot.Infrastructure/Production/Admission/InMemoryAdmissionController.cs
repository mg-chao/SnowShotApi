using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using SnowShot.Application;
using SnowShot.Infrastructure.Telemetry;

namespace SnowShot.Infrastructure.Admission;

public sealed class InMemoryAdmissionController(TimeProvider timeProvider) : IAdmissionController
{
    private readonly ConcurrentDictionary<string, ResourceState> _resources = new(StringComparer.Ordinal);

    public async Task<IAdmissionLease> AcquireAsync(AdmissionRequest request, CancellationToken cancellationToken)
    {
        using var activity = SnowShotTelemetry.Activities.StartActivity("admission.acquire");
        var state = _resources.GetOrAdd(request.Resource, _ => new());
        var started = Stopwatch.GetTimestamp();
        QueuedRequest? queued = null;
        lock (state.Gate)
        {
            state.Expire(timeProvider.GetUtcNow());
            var cutoff = timeProvider.GetUtcNow().AddMinutes(-1);
            if (!state.Rates.TryGetValue(request.PrincipalKey, out var timestamps))
                state.Rates[request.PrincipalKey] = timestamps = new();
            while (timestamps.Count > 0 && timestamps.Peek() <= cutoff) timestamps.Dequeue();
            if (timestamps.Count >= request.Policy.RequestsPerMinute)
                return Rejected(AdmissionRejectionReason.RateLimit, timestamps.Peek().AddMinutes(1) - timeProvider.GetUtcNow());
            timestamps.Enqueue(timeProvider.GetUtcNow());
            if (state.Queue.Count == 0 && state.CanAcquire(request))
                return state.Acquire(request, timeProvider);
            if (state.Queue.Count(value => !value.Cancelled) >= request.Policy.GlobalQueueLength ||
                state.Queue.Count(value => !value.Cancelled &&
                    string.Equals(value.Request.PrincipalKey, request.PrincipalKey, StringComparison.Ordinal)) >=
                    request.Policy.PerPrincipalQueueLength)
                return Rejected(AdmissionRejectionReason.QueueFull, TimeSpan.FromSeconds(1));
            queued = new(request);
            state.Queue.Enqueue(queued);
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (request.QueueWait <= TimeSpan.Zero) deadline.Cancel(); else deadline.CancelAfter(request.QueueWait);
        try
        {
            var lease = await queued.Completion.Task.WaitAsync(deadline.Token);
            SnowShotTelemetry.QueueWaitMilliseconds.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return lease;
        }
        catch (OperationCanceledException)
        {
            lock (state.Gate)
            {
                queued.Cancelled = true;
                state.Drain(timeProvider);
            }
            if (cancellationToken.IsCancellationRequested) throw;
            return Rejected(AdmissionRejectionReason.QueueTimeout, TimeSpan.FromSeconds(1));
        }
    }

    public Task<bool> CheckReadyAsync(CancellationToken cancellationToken) => Task.FromResult(true);

    private static MemoryAdmissionLease Rejected(AdmissionRejectionReason reason, TimeSpan retry) =>
        new(false, string.Empty, retry > TimeSpan.Zero ? retry : TimeSpan.FromSeconds(1), reason, null, null, null);

    private sealed class ResourceState
    {
        public object Gate { get; } = new();
        public Dictionary<string, Queue<DateTimeOffset>> Rates { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ActiveLease> Active { get; } = new(StringComparer.Ordinal);
        public Queue<QueuedRequest> Queue { get; } = new();

        public bool CanAcquire(AdmissionRequest request) =>
            Active.Count < request.Policy.GlobalConcurrency &&
            Active.Values.Count(value => value.Principal == request.PrincipalKey) < request.Policy.PerPrincipalConcurrency;

        public MemoryAdmissionLease Acquire(AdmissionRequest request, TimeProvider timeProvider)
        {
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var lease = new ActiveLease(request.PrincipalKey, timeProvider.GetUtcNow().Add(request.LeaseTtl));
            Active[token] = lease;
            SnowShotTelemetry.ActiveLeases.Add(1);
            return new(true, token, null, AdmissionRejectionReason.None, this, request, timeProvider);
        }

        public void Expire(DateTimeOffset now)
        {
            foreach (var pair in Active.Where(value => value.Value.ExpiresAt <= now).ToArray())
            {
                Active.Remove(pair.Key);
                pair.Value.Lost.Cancel();
                SnowShotTelemetry.LostLeases.Add(1);
                SnowShotTelemetry.ActiveLeases.Add(-1);
            }
        }

        public void Drain(TimeProvider timeProvider)
        {
            Expire(timeProvider.GetUtcNow());
            while (Queue.TryPeek(out var queued))
            {
                if (queued.Cancelled) { Queue.Dequeue(); continue; }
                if (!CanAcquire(queued.Request)) return;
                Queue.Dequeue();
                queued.Completion.TrySetResult(Acquire(queued.Request, timeProvider));
            }
        }
    }

    private sealed record ActiveLease(string Principal, DateTimeOffset ExpiresAt)
    {
        public CancellationTokenSource Lost { get; } = new();
        public DateTimeOffset ExpiresAt { get; set; } = ExpiresAt;
    }

    private sealed class QueuedRequest(AdmissionRequest request)
    {
        public AdmissionRequest Request { get; } = request;
        public TaskCompletionSource<MemoryAdmissionLease> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Cancelled { get; set; }
    }

    private sealed class MemoryAdmissionLease(
        bool acquired,
        string ownerToken,
        TimeSpan? retryAfter,
        AdmissionRejectionReason rejectionReason,
        ResourceState? state,
        AdmissionRequest? request,
        TimeProvider? timeProvider) : IAdmissionLease
    {
        private int _released;
        public bool Acquired { get; } = acquired;
        public string OwnerToken { get; } = ownerToken;
        public TimeSpan? RetryAfter { get; } = retryAfter;
        public AdmissionRejectionReason RejectionReason { get; } = rejectionReason;
        public CancellationToken OwnershipLost
        {
            get
            {
                if (state is null) return CancellationToken.None;
                lock (state.Gate) return state.Active.TryGetValue(OwnerToken, out var active) ? active.Lost.Token : new(true);
            }
        }

        public Task<bool> RenewAsync(CancellationToken cancellationToken)
        {
            if (!Acquired || state is null || request is null || timeProvider is null || Volatile.Read(ref _released) != 0) return Task.FromResult(false);
            lock (state.Gate)
            {
                state.Expire(timeProvider.GetUtcNow());
                if (!state.Active.TryGetValue(OwnerToken, out var active)) return Task.FromResult(false);
                active.ExpiresAt = timeProvider.GetUtcNow().Add(request.LeaseTtl);
                SnowShotTelemetry.RenewedLeases.Add(1);
                return Task.FromResult(true);
            }
        }

        public Task ReleaseAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0 || state is null || timeProvider is null) return Task.CompletedTask;
            lock (state.Gate)
            {
                if (state.Active.Remove(OwnerToken, out var active))
                {
                    active.Lost.Dispose();
                    SnowShotTelemetry.ActiveLeases.Add(-1);
                }
                state.Drain(timeProvider);
            }
            return Task.CompletedTask;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await ReleaseAsync(timeout.Token);
        }
    }
}
