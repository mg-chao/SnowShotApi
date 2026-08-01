using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShotApi.Tests.Domain;

public sealed class OperationLifecycleTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewalFailureCancelsOwnershipAndDisposalDoesNotLeakTheFailure(bool throws)
    {
        var admission = new RenewalLease(throws);
        var ledger = new RenewalLedger();
        var telemetry = new RenewalTelemetry();
        await using var scope = new OperationScope(Handle(), admission, ledger,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5),
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)), telemetry);

        Assert.Null(await scope.DispatchAsync(TestContext.Current.CancellationToken));
        await WaitForCancellationAsync(scope.OwnershipLost);

        Assert.True(scope.OwnershipLost.IsCancellationRequested);
        Assert.Single(telemetry.Reasons);
    }

    [Fact]
    public async Task DatabaseRenewalLossFailsClosed()
    {
        var admission = new RenewalLease(false, renews: true);
        var ledger = new RenewalLedger { RenewalResult = OwnershipMutationResult.LeaseLost };
        var telemetry = new RenewalTelemetry();
        await using var scope = new OperationScope(Handle(), admission, ledger,
            TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(5),
            LifecycleTimeouts.Defaults, telemetry);

        Assert.Null(await scope.DispatchAsync(TestContext.Current.CancellationToken));
        await WaitForCancellationAsync(scope.OwnershipLost);

        Assert.Contains("database_renewal", telemetry.Reasons);
    }

    [Fact]
    public async Task SettlementExceptionIsFailClosedAndObservable()
    {
        var telemetry = new RenewalTelemetry();
        var ledger = new RenewalLedger { SettlementException = new InvalidOperationException("database unavailable") };
        await using var scope = new OperationScope(Handle(), new RenewalLease(false), ledger,
            TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), LifecycleTimeouts.Defaults, telemetry);

        var error = await scope.CompleteAsync(new OperationSettlement(scope.Handle, NanoYuan.Zero, NanoYuan.Zero,
            false, false, false, 0, 0, "failed"));

        Assert.Equal("settlement_failed", error?.Detail);
        var failure = Assert.Single(telemetry.Failures);
        Assert.Equal(("settlement", "exception"), (failure.Stage, failure.Reason));
        Assert.Same(ledger.SettlementException, failure.Exception);
    }

    private static OperationHandle Handle()
    {
        var policy = ServicePolicy.Defaults();
        var resource = policy.Get(Resources.QwenFlash);
        return new(Guid.CreateVersion7(), new byte[32], 1, DateTimeOffset.UtcNow.AddMinutes(1),
            new(policy.Revision, policy.Fingerprint, resource.Resource, resource.Price, policy.PrincipalDailyAllowance,
                new(100), new(100)));
    }

    private static async Task WaitForCancellationAsync(CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, linked.Token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        Assert.Fail("Ownership was not cancelled within two seconds.");
    }

    private sealed class RenewalLease(bool throws, bool renews = false) : IAdmissionLease
    {
        public bool Acquired => true;
        public TimeSpan? RetryAfter => null;
        public AdmissionRejectionReason RejectionReason => AdmissionRejectionReason.None;
        public CancellationToken OwnershipLost => CancellationToken.None;
        public Task<bool> RenewAsync(CancellationToken cancellationToken) => throws
            ? Task.FromException<bool>(new InvalidOperationException("renew failed"))
            : Task.FromResult(renews);
        public Task ReleaseAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RenewalLedger : IOperationLedger
    {
        public OwnershipMutationResult RenewalResult { get; set; } = OwnershipMutationResult.Applied;
        public Exception? SettlementException { get; init; }
        public Task<OwnershipMutationResult> MarkDispatchedAsync(OperationHandle handle, TimeSpan leaseTtl, CancellationToken cancellationToken) => Task.FromResult(OwnershipMutationResult.Applied);
        public Task<OwnershipMutationResult> RenewAsync(OperationHandle handle, TimeSpan leaseTtl, CancellationToken cancellationToken) => Task.FromResult(RenewalResult);
        public Task<OperationReservation> ReserveAsync(ReserveOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnershipMutationResult> PrepareAttemptAsync(ProviderAttemptPreparation attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<OwnershipMutationResult> CompleteAttemptAsync(OperationHandle handle, ProviderAttempt attempt, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SettlementResult> CompleteAsync(OperationCompletion completion, CancellationToken cancellationToken) =>
            SettlementException is null
                ? throw new NotSupportedException()
                : Task.FromException<SettlementResult>(SettlementException);
    }

    private sealed class RenewalTelemetry : IOperationTelemetry
    {
        public List<string> Reasons { get; } = [];
        public List<(string Stage, string Reason, Exception Exception)> Failures { get; } = [];
        public void RenewalFailed(string reason) => Reasons.Add(reason);
        public void FencedMutation(string mutation, string result) { }
        public void AttemptCheckpointFailed(string stage, string reason) { }
        public void LifecycleFailed(string stage, string reason, Exception exception) => Failures.Add((stage, reason, exception));
    }
}
