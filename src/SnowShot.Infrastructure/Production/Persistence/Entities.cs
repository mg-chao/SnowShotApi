using SnowShot.Application;
using SnowShot.Domain;

namespace SnowShot.Infrastructure.Persistence;

internal sealed class PrincipalEntity
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

internal sealed class PrincipalFingerprintEntity
{
    public required byte[] Fingerprint { get; set; }
    public Guid PrincipalId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

internal sealed class AllowancePeriodEntity
{
    public Guid PrincipalId { get; set; }
    public DateOnly PeriodDate { get; set; }
    public long LimitNanoYuan { get; set; }
    public long CommittedNanoYuan { get; set; }
    public long ReservedNanoYuan { get; set; }
    public long AppliedPolicyRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal enum BudgetPeriodKind { Daily, Monthly }

internal sealed class OperatorBudgetPeriodEntity
{
    public BudgetPeriodKind Kind { get; set; }
    public required string PeriodKey { get; set; }
    public long LimitNanoYuan { get; set; }
    public long CommittedNanoYuan { get; set; }
    public long ReservedNanoYuan { get; set; }
    public long AppliedPolicyRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class UsageOperationEntity
{
    public Guid Id { get; set; }
    public Guid PrincipalId { get; set; }
    public DateOnly AllowanceDate { get; set; }
    public UsageKind Kind { get; set; }
    public required string Resource { get; set; }
    public required byte[] IdempotencyHash { get; set; }
    public required byte[] OwnerToken { get; set; }
    public long Fence { get; set; }
    public required byte[] PolicyFingerprint { get; set; }
    public long PolicyRevision { get; set; }
    public long InputRateNanoYuan { get; set; }
    public long OutputRateNanoYuan { get; set; }
    public long AllowanceLimitNanoYuan { get; set; }
    public long ReservedPublicNanoYuan { get; set; }
    public long ReservedOperatorNanoYuan { get; set; }
    public long ActualPublicNanoYuan { get; set; }
    public long ActualOperatorNanoYuan { get; set; }
    public long OperatorOverageNanoYuan { get; set; }
    public ReservationState State { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset AbsoluteDeadline { get; set; }
    public DateTimeOffset LeaseExpiresAt { get; set; }
    public DateTimeOffset? DispatchedAt { get; set; }
    public DateTimeOffset? SettledAt { get; set; }
    public byte[]? SettlementFingerprint { get; set; }
}

internal sealed class PolicyRevisionEntity
{
    public long Revision { get; set; }
    public required byte[] Fingerprint { get; set; }
    public required string CanonicalDocument { get; set; }
    public long PrincipalDailyAllowanceNanoYuan { get; set; }
    public long DailyOperatorBudgetNanoYuan { get; set; }
    public long MonthlyOperatorBudgetNanoYuan { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
}

internal sealed class PolicyStateEntity
{
    public short Id { get; set; }
    public long? ActiveRevision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

internal sealed class ProviderAttemptEntity
{
    public Guid Id { get; set; }
    public Guid OperationId { get; set; }
    public int AttemptNumber { get; set; }
    public required string Provider { get; set; }
    public required string Resource { get; set; }
    public ProviderAttemptState State { get; set; }
    public AttemptDispatchState DispatchState { get; set; }
    public string? Outcome { get; set; }
    public int? HttpStatus { get; set; }
    public long InputUnits { get; set; }
    public long OutputUnits { get; set; }
    public long CostNanoYuan { get; set; }
    public bool CostKnown { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

internal enum ProviderAttemptState { Prepared, Completed }

internal sealed class UsageEventEntity
{
    public long Id { get; set; }
    public Guid OperationId { get; set; }
    public Guid PrincipalId { get; set; }
    public UsageKind Kind { get; set; }
    public required string Resource { get; set; }
    public required string Outcome { get; set; }
    public long InputUnits { get; set; }
    public long OutputUnits { get; set; }
    public long PublicCostNanoYuan { get; set; }
    public long OperatorCostNanoYuan { get; set; }
    public long OperatorOverageNanoYuan { get; set; }
    public bool CostKnown { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}

internal sealed class DailyAggregateEntity
{
    public DateOnly UsageDate { get; set; }
    public UsageKind Kind { get; set; }
    public required string Resource { get; set; }
    public long Requests { get; set; }
    public long UnknownCostRequests { get; set; }
    public long InputUnits { get; set; }
    public long OutputUnits { get; set; }
    public long PublicCostNanoYuan { get; set; }
    public long OperatorCostNanoYuan { get; set; }
    public long OperatorOverageNanoYuan { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
