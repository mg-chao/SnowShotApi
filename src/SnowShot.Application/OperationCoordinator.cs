using System.Security.Cryptography;
using System.Text;
using SnowShot.Domain;

namespace SnowShot.Application;

public sealed class OperationCoordinator(
    IPrincipalIdentity identity,
    IAdmissionController admission,
    IOperationLedger ledger,
    ISystemClock clock,
    ServicePolicy policy,
    LifecycleTimeouts timeouts,
    IOperationTelemetry telemetry)
{
    public async Task<ApplicationResult<OperationScope>> StartAsync(
        string resource,
        UsageKind kind,
        RequestContext context,
        NanoYuan publicReservation,
        NanoYuan operatorMaximum,
        CancellationToken cancellationToken)
    {
        var principal = await identity.ResolveAsync(context.ClientAddress, cancellationToken);
        if (principal is null)
            return ApplicationResult.Failure<OperationScope>(new(ApplicationErrorCode.IdentityUnavailable, "client_identity_unavailable"));

        var resourcePolicy = policy.Get(resource);
        var admissionLease = await admission.AcquireAsync(new AdmissionRequest(
            resource, principal.AdmissionKey, resourcePolicy.Admission,
            resourcePolicy.Admission.QueueWait, policy.ActiveLeaseTtl), cancellationToken);
        if (!admissionLease.Acquired)
        {
            var error = AdmissionFailure(admissionLease);
            await DisposeLeaseAsync(admissionLease);
            return ApplicationResult.Failure<OperationScope>(error);
        }

        var now = clock.UtcNow;
        var ownerToken = RandomNumberGenerator.GetBytes(32);
        var snapshot = new ReservationSnapshot(policy.Revision, policy.Fingerprint, resource, resourcePolicy.Price,
            policy.PrincipalDailyAllowance, publicReservation, NanoYuan.Min(operatorMaximum, resourcePolicy.OperatorMaximum));
        var operation = new ReserveOperation(
            Guid.CreateVersion7(now), principal.Id, kind, snapshot,
            HashIdempotency(resource, principal.Id, context.ClientRequestId), ownerToken,
            resourcePolicy.ExecutionDeadline, policy.ActiveLeaseTtl);
        OperationReservation reservation;
        try
        {
            reservation = await ledger.ReserveAsync(operation, cancellationToken);
        }
        catch
        {
            await DisposeLeaseAsync(admissionLease);
            throw;
        }
        if (!reservation.Accepted || reservation.Handle is null)
        {
            await DisposeLeaseAsync(admissionLease);
            return ApplicationResult.Failure<OperationScope>(ReservationFailure(reservation.RejectionReason, reservation.RetryAfter));
        }

        return ApplicationResult.Success(new OperationScope(
            reservation.Handle, admissionLease, ledger, policy.ActiveLeaseTtl,
            policy.LeaseRenewalInterval, timeouts, telemetry));
    }

    private async Task DisposeLeaseAsync(IAdmissionLease lease)
    {
        using var timeout = new CancellationTokenSource(timeouts.Cleanup);
        try { await lease.ReleaseAsync(timeout.Token); }
        catch (Exception exception) { telemetry.LifecycleFailed("admission_cleanup", "release", exception); }
        try { await lease.DisposeAsync(); }
        catch (Exception exception) { telemetry.LifecycleFailed("admission_cleanup", "dispose", exception); }
    }

    private static byte[] HashIdempotency(string operation, Guid principalId, string requestId) =>
        SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}\n{principalId:D}\n{requestId}"));

    private static ApplicationError AdmissionFailure(IAdmissionLease lease) => lease.RejectionReason switch
    {
        AdmissionRejectionReason.RateLimit or AdmissionRejectionReason.PrincipalConcurrency =>
            new(ApplicationErrorCode.RateLimited, lease.RejectionReason.ToString(), lease.RetryAfter),
        AdmissionRejectionReason.QueueFull or AdmissionRejectionReason.QueueTimeout =>
            new(ApplicationErrorCode.QueueFull, lease.RejectionReason.ToString(), lease.RetryAfter),
        _ => new(ApplicationErrorCode.DependencyUnavailable, "admission_unavailable", lease.RetryAfter),
    };

    private static ApplicationError ReservationFailure(ReservationRejectionReason reason, TimeSpan? retryAfter) => reason switch
    {
        ReservationRejectionReason.DuplicateRequest => new(ApplicationErrorCode.DuplicateRequest, "duplicate_request"),
        ReservationRejectionReason.OperatorBudgetExhausted => new(ApplicationErrorCode.OperatorBudgetExhausted, "operator_budget_exhausted", retryAfter),
        ReservationRejectionReason.PolicyUnavailable => new(ApplicationErrorCode.PolicyUnavailable, "policy_unavailable"),
        _ => new(ApplicationErrorCode.AllowanceExhausted, "allowance_exhausted", retryAfter),
    };
}

public sealed class OperationScope : IAsyncDisposable
{
    private readonly IAdmissionLease _admission;
    private readonly IOperationLedger _ledger;
    private readonly TimeSpan _leaseTtl;
    private readonly TimeSpan _renewalInterval;
    private readonly LifecycleTimeouts _timeouts;
    private readonly IOperationTelemetry _telemetry;
    private readonly CancellationTokenSource _ownership = new();
    private readonly CancellationTokenRegistration _admissionLost;
    private Task? _renewal;
    private int _ownershipLost;
    private int _disposed;

    public OperationScope(
        OperationHandle handle,
        IAdmissionLease admission,
        IOperationLedger ledger,
        TimeSpan leaseTtl,
        TimeSpan renewalInterval,
        LifecycleTimeouts timeouts,
        IOperationTelemetry telemetry)
    {
        Handle = handle;
        _admission = admission;
        _ledger = ledger;
        _leaseTtl = leaseTtl;
        _renewalInterval = renewalInterval;
        _timeouts = timeouts;
        _telemetry = telemetry;
        _admissionLost = admission.OwnershipLost.Register(() => LoseOwnership("admission_expired"));
    }

    public OperationHandle Handle { get; }
    public CancellationToken OwnershipLost => _ownership.Token;

    public async Task<ApplicationError?> DispatchAsync(CancellationToken cancellationToken)
    {
        var result = await _ledger.MarkDispatchedAsync(Handle, _leaseTtl, cancellationToken);
        _telemetry.FencedMutation("dispatch", result.ToString());
        if (result != OwnershipMutationResult.Applied)
        {
            LoseOwnership("database_dispatch");
            return new(ApplicationErrorCode.LeaseLost, "database_lease_lost");
        }
        _renewal = RenewLoopAsync(_ownership.Token);
        return null;
    }

    public async Task<ApplicationResult<ProviderAttemptPreparation>> PrepareAttemptAsync(
        int attemptNumber,
        string provider,
        string resource,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var preparation = new ProviderAttemptPreparation(
            Guid.CreateVersion7(startedAt), Handle, attemptNumber, provider, resource, startedAt);
        try
        {
            var result = await _ledger.PrepareAttemptAsync(preparation, cancellationToken);
            _telemetry.FencedMutation("attempt_prepare", result.ToString());
            if (result == OwnershipMutationResult.Applied)
                return ApplicationResult.Success(preparation);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            _telemetry.AttemptCheckpointFailed("prepare", "exception");
            return ApplicationResult.Failure<ProviderAttemptPreparation>(
                new(ApplicationErrorCode.DependencyUnavailable, "attempt_prepare_failed"));
        }

        LoseOwnership("attempt_prepare");
        return ApplicationResult.Failure<ProviderAttemptPreparation>(
            new(ApplicationErrorCode.LeaseLost, "database_lease_lost"));
    }

    public async Task<ApplicationError?> CompleteAttemptAsync(ProviderAttempt attempt, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeouts.AttemptRecording);
        try
        {
            var result = await _ledger.CompleteAttemptAsync(Handle, attempt, timeout.Token);
            _telemetry.FencedMutation("attempt_complete", result.ToString());
            if (result == OwnershipMutationResult.Applied) return null;
            LoseOwnership("attempt_complete");
            return new(ApplicationErrorCode.LeaseLost, "database_lease_lost");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException)
        {
            _telemetry.AttemptCheckpointFailed("complete", "timeout");
            return new(ApplicationErrorCode.DependencyUnavailable, "attempt_recording_timeout");
        }
        catch (Exception)
        {
            _telemetry.AttemptCheckpointFailed("complete", "exception");
            return new(ApplicationErrorCode.DependencyUnavailable, "attempt_recording_failed");
        }
    }

    public async Task<ApplicationError?> CompleteAsync(OperationSettlement settlement, ProviderAttempt? finalAttempt = null)
    {
        using var timeout = new CancellationTokenSource(_timeouts.Settlement);
        try
        {
            var result = await _ledger.CompleteAsync(new(settlement, finalAttempt), timeout.Token);
            return result.Accepted ? null : result.RejectionReason == SettlementRejectionReason.LeaseLost
                ? new(ApplicationErrorCode.LeaseLost, "database_lease_lost")
                : new(ApplicationErrorCode.DependencyUnavailable, "settlement_conflict");
        }
        catch (OperationCanceledException exception)
        {
            _telemetry.LifecycleFailed("settlement", "timeout", exception);
            return new(ApplicationErrorCode.DependencyUnavailable, "settlement_timeout");
        }
        catch (Exception exception)
        {
            _telemetry.LifecycleFailed("settlement", "exception", exception);
            return new(ApplicationErrorCode.DependencyUnavailable, "settlement_failed");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        LoseOwnership("disposed", report: false);
        if (_renewal is not null)
        {
            try { await _renewal; }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException) { }
        }
        using var timeout = new CancellationTokenSource(_timeouts.Cleanup);
        try { await _admission.ReleaseAsync(timeout.Token); }
        catch (Exception exception) { _telemetry.LifecycleFailed("admission_cleanup", "release", exception); }
        try { await _admission.DisposeAsync(); }
        catch (Exception exception) { _telemetry.LifecycleFailed("admission_cleanup", "dispose", exception); }
        _admissionLost.Dispose();
        _ownership.Dispose();
    }

    private async Task RenewLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_renewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                bool admissionOwned;
                try { admissionOwned = await _admission.RenewAsync(cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    _telemetry.LifecycleFailed("renewal", "admission_exception", exception);
                    LoseOwnership("admission_exception");
                    return;
                }
                if (!admissionOwned) { LoseOwnership("admission_renewal"); return; }

                OwnershipMutationResult result;
                try { result = await _ledger.RenewAsync(Handle, _leaseTtl, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception exception)
                {
                    _telemetry.LifecycleFailed("renewal", "database_exception", exception);
                    LoseOwnership("database_exception");
                    return;
                }
                _telemetry.FencedMutation("renewal", result.ToString());
                if (result != OwnershipMutationResult.Applied) { LoseOwnership("database_renewal"); return; }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _telemetry.LifecycleFailed("renewal", "supervisor", exception);
            LoseOwnership("renewal_supervisor");
        }
    }

    private void LoseOwnership(string reason, bool report = true)
    {
        if (Interlocked.Exchange(ref _ownershipLost, 1) != 0) return;
        if (report) _telemetry.RenewalFailed(reason);
        try { _ownership.Cancel(); } catch (ObjectDisposedException) { }
    }
}
