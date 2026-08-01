using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace SnowShot.Infrastructure.Telemetry;

public static class SnowShotTelemetry
{
    public const string Name = "SnowShot";
    public static readonly ActivitySource Activities = new(Name);
    public static readonly Meter Meter = new(Name);
    public static readonly Histogram<double> QueueWaitMilliseconds = Meter.CreateHistogram<double>("snowshot.admission.queue_wait.ms");
    public static readonly Counter<long> StaleQueueTickets = Meter.CreateCounter<long>("snowshot.admission.queue.stale_evictions");
    public static readonly Counter<long> PrincipalQueueRejections = Meter.CreateCounter<long>("snowshot.admission.queue.principal_rejections");
    public static readonly UpDownCounter<long> ActiveLeases = Meter.CreateUpDownCounter<long>("snowshot.leases.active");
    public static readonly Counter<long> RenewedLeases = Meter.CreateCounter<long>("snowshot.leases.renewed");
    public static readonly Counter<long> LostLeases = Meter.CreateCounter<long>("snowshot.leases.lost");
    public static readonly Counter<long> ProviderAttempts = Meter.CreateCounter<long>("snowshot.provider.attempts");
    public static readonly Histogram<double> ProviderLatencyMilliseconds = Meter.CreateHistogram<double>("snowshot.provider.latency.ms");
    public static readonly Counter<long> PublicCost = Meter.CreateCounter<long>("snowshot.cost.public.nanoyuan");
    public static readonly Counter<long> OperatorCost = Meter.CreateCounter<long>("snowshot.cost.operator.nanoyuan");
    public static readonly Counter<long> UnknownCost = Meter.CreateCounter<long>("snowshot.cost.unknown.operations");
    public static readonly Counter<long> Overage = Meter.CreateCounter<long>("snowshot.cost.overage.nanoyuan");
    public static readonly Counter<long> Reconciliations = Meter.CreateCounter<long>("snowshot.reconciliations");
    public static readonly Counter<long> ReconciliationOutcomes = Meter.CreateCounter<long>("snowshot.reconciliation.outcomes");
    public static readonly Histogram<long> ReconciliationBacklog = Meter.CreateHistogram<long>("snowshot.reconciliation.backlog.operations");
    public static readonly Histogram<double> ReconciliationOldestAgeSeconds = Meter.CreateHistogram<double>("snowshot.reconciliation.backlog.oldest_age.seconds");
    public static readonly Counter<long> FencedMutations = Meter.CreateCounter<long>("snowshot.operations.fenced_mutations");
    public static readonly Counter<long> RenewalFailures = Meter.CreateCounter<long>("snowshot.operations.renewal_failures");
    public static readonly Counter<long> IdentityIntegrityConflicts = Meter.CreateCounter<long>("snowshot.identity.integrity_conflicts");
    public static readonly Counter<long> DuplicateRequests = Meter.CreateCounter<long>("snowshot.operations.duplicate_requests");
    public static readonly Counter<long> WorkerBusy = Meter.CreateCounter<long>("snowshot.worker.busy");
    public static readonly Counter<long> CircuitOpen = Meter.CreateCounter<long>("snowshot.provider.circuit_open");
    public static readonly Counter<long> AttemptCheckpointFailures = Meter.CreateCounter<long>("snowshot.provider.attempt_checkpoint_failures");
    public static readonly Counter<long> LifecycleFailures = Meter.CreateCounter<long>("snowshot.operations.lifecycle_failures");
    public static readonly Counter<long> AdmissionCleanupFailures = Meter.CreateCounter<long>("snowshot.admission.cleanup_failures");
    public static readonly Counter<long> AdmissionDependencyFailures = Meter.CreateCounter<long>("snowshot.admission.dependency_failures");
    public static readonly Counter<long> PolicyActivation = Meter.CreateCounter<long>("snowshot.policy.activation");
    public static readonly Counter<long> StalePolicyReplicas = Meter.CreateCounter<long>("snowshot.policy.stale_replicas");
    public static readonly Counter<long> PolicyReservationRejections = Meter.CreateCounter<long>("snowshot.policy.reservation_rejections");
    public static readonly Counter<long> ProviderAccessSelections = Meter.CreateCounter<long>("snowshot.provider_access.selections");
    public static readonly UpDownCounter<long> ProviderAccessActive = Meter.CreateUpDownCounter<long>("snowshot.provider_access.active");
    public static readonly Counter<long> ProviderAccessSaturation = Meter.CreateCounter<long>("snowshot.provider_access.saturation");
    public static readonly Counter<long> ProviderAccessDependencyFailures = Meter.CreateCounter<long>("snowshot.provider_access.dependency_failures");
    public static readonly Counter<long> ProviderRetries = Meter.CreateCounter<long>("snowshot.provider.retries");
    public static readonly Histogram<long> TranslationBatchSize = Meter.CreateHistogram<long>("snowshot.translation.batch_size");
    public static readonly UpDownCounter<long> TranslationActiveConversations =
        Meter.CreateUpDownCounter<long>("snowshot.translation.active_conversations");
}

public sealed class OperationTelemetry(ILogger<OperationTelemetry> logger) : SnowShot.Application.IOperationTelemetry
{
    private static readonly Action<ILogger, string, string, Exception?> LifecycleFailure =
        LoggerMessage.Define<string, string>(LogLevel.Error, new EventId(5301, nameof(LifecycleFailure)),
            "Operation lifecycle failure at {Stage}: {Reason}");

    public void RenewalFailed(string reason) => SnowShotTelemetry.RenewalFailures.Add(1,
        new KeyValuePair<string, object?>[] { new("reason", reason) });
    public void FencedMutation(string mutation, string result) =>
        SnowShotTelemetry.FencedMutations.Add(1, new("mutation", mutation), new("result", result));
    public void AttemptCheckpointFailed(string stage, string reason) =>
        SnowShotTelemetry.AttemptCheckpointFailures.Add(1, new("stage", stage), new("reason", reason));
    public void LifecycleFailed(string stage, string reason, Exception exception)
    {
        SnowShotTelemetry.LifecycleFailures.Add(1, new("stage", stage), new("reason", reason));
        LifecycleFailure(logger, stage, reason, exception);
    }
}

public sealed class TranslationTelemetry : SnowShot.Application.ITranslationTelemetry
{
    public void BatchStarted(int batchSize) => SnowShotTelemetry.TranslationBatchSize.Record(batchSize);
}
