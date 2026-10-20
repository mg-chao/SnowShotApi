using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShot.Api;

public sealed record DeploymentDescription(
    long PolicyRevision,
    string PolicyFingerprint,
    int MaximumRequestSeconds,
    int DrainTimeoutSeconds,
    int ShutdownTimeoutSeconds)
{
    public static DeploymentDescription From(ServicePolicy policy)
    {
        var requestSeconds = (int)Math.Ceiling(policy.ResourcePolicies.Max(value =>
            (value.Admission.QueueWait + value.ExecutionDeadline).TotalSeconds));
        var lifecycle = LifecycleTimeouts.Defaults;
        var drainSeconds = (int)Math.Ceiling(requestSeconds +
            lifecycle.Cleanup.TotalSeconds + lifecycle.AttemptRecording.TotalSeconds +
            lifecycle.Settlement.TotalSeconds + 10);
        return new(policy.Revision, policy.Fingerprint, requestSeconds, Math.Max(360, drainSeconds), 360);
    }
}
