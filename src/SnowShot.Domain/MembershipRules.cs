using System.Collections.Immutable;

namespace SnowShot.Domain;

public static class MembershipRules
{
    public const long NanoYuanPerFen = 10_000_000;
    public const long FreeRecognitionThresholdNanoYuan = 18800 * NanoYuanPerFen;

    public static ImmutableArray<(string Id, int Fen)> Tiers { get; } =
        [("cny-9-9", 990), ("cny-29-9", 2990), ("cny-88-8", 8880), ("cny-188", 18800), ("cny-999", 99900)];

    public static string? TierFor(long cumulativeRechargeNanoYuan)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cumulativeRechargeNanoYuan);
        for (var index = Tiers.Length - 1; index >= 0; index--)
            if (cumulativeRechargeNanoYuan >= Tiers[index].Fen * NanoYuanPerFen)
                return Tiers[index].Id;
        return null;
    }

    public static bool HasFreeRecognition(long cumulativeRechargeNanoYuan)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cumulativeRechargeNanoYuan);
        return cumulativeRechargeNanoYuan >= FreeRecognitionThresholdNanoYuan;
    }
}
