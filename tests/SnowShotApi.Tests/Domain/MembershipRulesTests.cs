using SnowShot.Domain;

namespace SnowShotApi.Tests.Domain;

public sealed class MembershipRulesTests
{
    [Theory]
    [InlineData(0, null, false)]
    [InlineData(10, null, false)]
    [InlineData(989, null, false)]
    [InlineData(990, "cny-9-9", false)]
    [InlineData(2989, "cny-9-9", false)]
    [InlineData(2990, "cny-29-9", false)]
    [InlineData(8879, "cny-29-9", false)]
    [InlineData(8880, "cny-88-8", false)]
    [InlineData(18799, "cny-88-8", false)]
    [InlineData(18800, "cny-188", true)]
    [InlineData(99899, "cny-188", true)]
    [InlineData(99900, "cny-999", true)]
    [InlineData(100000, "cny-999", true)]
    public void CumulativeRechargeSelectsHighestReachedTier(long fen, string? tier, bool freeRecognition)
    {
        var amount = fen * MembershipRules.NanoYuanPerFen;
        Assert.Equal(tier, MembershipRules.TierFor(amount));
        Assert.Equal(freeRecognition, MembershipRules.HasFreeRecognition(amount));
    }

    [Fact]
    public void NegativeCumulativeRechargeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MembershipRules.TierFor(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => MembershipRules.HasFreeRecognition(-1));
    }
}
