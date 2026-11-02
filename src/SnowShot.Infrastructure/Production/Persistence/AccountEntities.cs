namespace SnowShot.Infrastructure.Persistence;

internal sealed class AccountEntity
{
    public Guid Id { get; set; }
    public required string Username { get; set; }
    public required string NormalizedUsername { get; set; }
    public required string PasswordHash { get; set; }
    public bool Active { get; set; }
    public bool IsTemporary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public long ForfeitedCreditNanoYuan { get; set; }
    public string? DeletionConsentVersion { get; set; }
}
internal sealed class AccountUsernameClaimEntity
{
    public required string NormalizedUsername { get; set; }
    public Guid AccountId { get; set; }
    public Guid RequestId { get; set; }
}
internal sealed class AccountWalletEntity
{
    public Guid AccountId { get; set; }
    public long CumulativeRechargeNanoYuan { get; set; }
    public long AvailableNanoYuan { get; set; }
    public long HeldNanoYuan { get; set; }
    public long OutstandingNanoYuan { get; set; }
}
internal sealed class AccountFinancialEntryEntity
{
    public required string Id { get; set; }
    public Guid AccountId { get; set; }
    public required string Type { get; set; }
    public long AmountNanoYuan { get; set; }
    public required string Status { get; set; }
    public string? OrderId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string Description { get; set; }
}
internal sealed class AccountUsageDedupEntity
{
    public required byte[] Hash { get; set; }
    public Guid AccountId { get; set; }
    public Guid OperationId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
internal sealed class AccountWechatLinkEntity
{
    public required string OpenId { get; set; }
    public Guid AccountId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
internal sealed class AccountPayerClaimEntity
{
    public Guid AccountId { get; set; }
    public required string OpenId { get; set; }
    public Guid RequestId { get; set; }
}
internal sealed class AccountSessionEntity
{
    public Guid Id { get; set; }
    public Guid? AccountId { get; set; }
    public string? OpenId { get; set; }
    public string? WechatSessionKeyEncrypted { get; set; }
    public required byte[] AccessHash { get; set; }
    public required byte[] RefreshHash { get; set; }
    public DateTimeOffset AccessExpiresAt { get; set; }
    public DateTimeOffset RefreshExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
internal sealed class AccountPurchaseRequestEntity
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    public required string Purpose { get; set; }
    public required string State { get; set; }
    public string? PayerOpenId { get; set; }
    public string? CompletionKind { get; set; }
    public byte[]? ProofHash { get; set; }
    public byte[]? CodeImage { get; set; }
    public DateTimeOffset GeneratedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string? OrderId { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
    public Guid? BootstrapSessionId { get; set; }
    public string? BootstrapTokensEncrypted { get; set; }
}
internal sealed class AccountPaymentOrderEntity
{
    public Guid? CheckoutAttemptId { get; set; }
    public DateTimeOffset? NextDeliveryAttemptAt { get; set; }
    public Guid? DeliveryLeaseToken { get; set; }
    public DateTimeOffset? DeliveryLeaseExpiresAt { get; set; }
    public int DeliveryAttempts { get; set; }
    public required string Id { get; set; }
    public Guid RequestId { get; set; }
    public Guid AccountId { get; set; }
    public required string OpenId { get; set; }
    public required string PackageId { get; set; }
    public int PriceFen { get; set; }
    public int? PaidFen { get; set; }
    public required string State { get; set; }
    public string? PlatformTransactionId { get; set; }
    public required string PaymentParametersEncrypted { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? LastQueriedAt { get; set; }
    public bool Fulfilled { get; set; }
    public bool DeliveryConfirmed { get; set; }
}
internal sealed class AccountRefundEntity
{
    public DateTimeOffset? NextQueryAt { get; set; }
    public Guid? QueryLeaseToken { get; set; }
    public DateTimeOffset? QueryLeaseExpiresAt { get; set; }
    public int QueryAttempts { get; set; }
    public required string Id { get; set; }
    public required string OrderId { get; set; }
    public int RefundFen { get; set; }
    public required string State { get; set; }
    public bool Applied { get; set; }
    public bool SuccessNotified { get; set; }
    public required string PlatformRefundId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
internal sealed class AccountCaptchaEntity
{
    public Guid Id { get; set; }
    public required byte[] AnswerHash { get; set; }
    public required string ClientKey { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Consumed { get; set; }
    public Guid? ValidationReceipt { get; set; }
}
internal sealed class AccountThrottleEntity
{
    public required string Id { get; set; }
    public DateTimeOffset PeriodStart { get; set; }
    public int Count { get; set; }
}
internal sealed class AccountPlatformEventEntity
{
    public required string Id { get; set; }
    public required string EventName { get; set; }
    public string? MerchantOrderId { get; set; }
    public bool RoutingMetadataComplete { get; set; }
    public DateTimeOffset? RoutingLastAttemptAt { get; set; }
    public required string PayloadEncrypted { get; set; }
    public required string State { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
}
