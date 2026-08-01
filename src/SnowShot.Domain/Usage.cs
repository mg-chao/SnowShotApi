using System.Security.Cryptography;
using System.Text;
using System.Collections.Immutable;

namespace SnowShot.Domain;

public enum UsageKind { Translation, Chat, TableExtraction }
public enum ReservationState { Reserved, Dispatched, Committed, Released, UnknownCost }

public static class ReservationStates
{
    public static bool IsActive(this ReservationState state) =>
        state is ReservationState.Reserved or ReservationState.Dispatched;

    public static bool IsTerminal(this ReservationState state) => !state.IsActive();
}

public sealed record ReservationSnapshot(
    long PolicyRevision,
    string PolicyFingerprint,
    string Resource,
    UnitPrice Price,
    NanoYuan AllowanceLimit,
    NanoYuan PublicReservation,
    NanoYuan OperatorMaximum);

public sealed record SettlementDecision(
    ReservationState State,
    NanoYuan PublicCost,
    NanoYuan OperatorCost,
    NanoYuan OperatorOverage,
    string Fingerprint);

public sealed record OperationHandle
{
    public OperationHandle(
        Guid operationId,
        ReadOnlySpan<byte> ownerToken,
        long fence,
        DateTimeOffset absoluteDeadline,
        ReservationSnapshot snapshot)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation ID is required.", nameof(operationId));
        if (ownerToken.Length != 32) throw new ArgumentException("Owner tokens must contain 32 bytes.", nameof(ownerToken));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fence);
        OperationId = operationId;
        OwnerToken = ImmutableArray.Create(ownerToken.ToArray());
        Fence = fence;
        AbsoluteDeadline = absoluteDeadline;
        Snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
    }

    public Guid OperationId { get; }
    public ImmutableArray<byte> OwnerToken { get; }
    public long Fence { get; }
    public DateTimeOffset AbsoluteDeadline { get; }
    public ReservationSnapshot Snapshot { get; }
}

public static class ReservationRules
{
    public static bool WouldExceed(long committed, long reserved, long requested, long limit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(committed);
        ArgumentOutOfRangeException.ThrowIfNegative(reserved);
        ArgumentOutOfRangeException.ThrowIfNegative(requested);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        if (committed > limit) return true;
        var remaining = limit - committed;
        if (reserved > remaining) return true;
        return requested > remaining - reserved;
    }

    public static bool CanTransition(ReservationState from, ReservationState to) => (from, to) switch
    {
        (ReservationState.Reserved, ReservationState.Dispatched or ReservationState.Released) => true,
        (ReservationState.Dispatched, ReservationState.Committed or ReservationState.Released or ReservationState.UnknownCost) => true,
        _ => false,
    };

    public static SettlementDecision Settle(
        ReservationState current,
        ReservationSnapshot snapshot,
        NanoYuan reportedPublic,
        NanoYuan reportedOperator,
        bool delivered,
        bool costKnown,
        bool verifiableOverage,
        long inputUnits,
        long outputUnits,
        string outcome)
    {
        if (!current.IsActive())
        {
            throw new InvalidOperationException($"Cannot settle an operation in state {current}.");
        }

        var publicCost = delivered ? reportedPublic : NanoYuan.Zero;
        var operatorCost = costKnown ? reportedOperator : snapshot.OperatorMaximum;
        if (operatorCost > snapshot.OperatorMaximum && !verifiableOverage)
        {
            operatorCost = snapshot.OperatorMaximum;
        }
        var overage = operatorCost > snapshot.OperatorMaximum
            ? new NanoYuan(operatorCost.Value - snapshot.OperatorMaximum.Value)
            : NanoYuan.Zero;
        var state = costKnown
            ? (delivered ? ReservationState.Committed : ReservationState.Released)
            : ReservationState.UnknownCost;
        var canonical = string.Join('|', state, publicCost.Value, operatorCost.Value, overage.Value,
            delivered, costKnown, verifiableOverage, inputUnits, outputUnits, outcome);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
        return new(state, publicCost, operatorCost, overage, fingerprint);
    }
}

public readonly record struct ExecutionDeadline(DateTimeOffset StartedAt, DateTimeOffset ExpiresAt)
{
    public static ExecutionDeadline Start(DateTimeOffset now, TimeSpan duration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        return new(now, now.Add(duration));
    }

    public TimeSpan Remaining(DateTimeOffset now) => now >= ExpiresAt ? TimeSpan.Zero : ExpiresAt - now;
}
