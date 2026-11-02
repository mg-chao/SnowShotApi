using SnowShot.Api;
using SnowShot.Domain;

namespace SnowShotApi.Tests.Architecture;

public sealed class DeploymentDescriptionTests
{
    [Fact]
    public void DescriptionUsesEffectivePolicyAndCoversQueueExecutionAndSettlement()
    {
        var policy = ServicePolicy.Defaults();

        var description = DeploymentDescription.From(policy);

        Assert.Equal(policy.Revision, description.PolicyRevision);
        Assert.Equal(policy.Fingerprint, description.PolicyFingerprint);
        Assert.Equal(330, description.MaximumRequestSeconds);
        Assert.Equal(360, description.DrainTimeoutSeconds);
        Assert.Equal(360, description.ShutdownTimeoutSeconds);
    }
}
