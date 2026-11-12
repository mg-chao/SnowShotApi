namespace SnowShot.Application;

public sealed record AccountSessionIdentity(Guid SessionId, Guid? AccountId, string? OpenId);
public interface IAccountSessionResolver
{
    Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken cancellationToken);
}

public sealed record AccountSessionTokens(string AccessToken, string RefreshToken, DateTimeOffset ExpiresAt,
    DateTimeOffset RefreshExpiresAt, Guid? AccountId);
public sealed record AccountCaptcha(Guid CaptchaId, string ImageDataUrl, DateTimeOffset ExpiresAt);
public sealed record RegisterAccountCommand(string Username, string Password, string PasswordConfirmation,
    Guid CaptchaId, string CaptchaAnswer);
public sealed record AccountSummary(bool Linked, Guid? AccountId, string? Username, string MembershipStatus,
    string AvailableCreditNanoYuan, string HeldCreditNanoYuan, string OutstandingBalanceNanoYuan, bool PaymentsAvailable,
    string CumulativeRechargeNanoYuan = "0", string? MembershipTier = null,
    bool FreeTableExtraction = false, bool FreeLatexExtraction = false, bool IsTemporary = false);
public sealed record MembershipPackage(string Id, int PriceFen, string CreditsNanoYuan);
public sealed record MembershipPackages(bool PaymentsAvailable, IReadOnlyList<MembershipPackage> Packages);
public sealed record AccountPurchaseRequest(Guid RequestId, string? Username, string Purpose, string State,
    DateTimeOffset GeneratedAt, DateTimeOffset ExpiresAt, DateTimeOffset ServerTime,
    string? MiniProgramCodeDataUrl, string? OrderId, bool PaymentsAvailable, string? RegistrationProof = null,
    string? CompletionKind = null, string? OrderState = null);
public sealed record PublicPurchaseRequest(Guid RequestId, string? Username, string Purpose, string State,
    DateTimeOffset ExpiresAt, DateTimeOffset ServerTime, bool PaymentsAvailable, string? OrderId = null,
    string? CompletionKind = null, string? OrderState = null);
public sealed record VirtualPaymentParameters(string Mode, string SignData, string PaySig, string Signature);
public sealed record AccountPurchaseOrder(string OrderId, Guid RequestId, string State, VirtualPaymentParameters? PaymentParameters);
public sealed record AccountOrderStatus(string OrderId, Guid RequestId, string State, string? RefundStatus);
public sealed record AccountTransaction(string Id, DateTimeOffset OccurredAt, string Type,
    string AmountNanoYuan, string Status, string? OrderId, string Description);
public sealed record AccountTransactions(IReadOnlyList<AccountTransaction> Items, string? NextCursor);
public sealed record AccountDeletionEligibility(bool CanDelete, IReadOnlyList<string> Blockers,
    string AvailableCreditNanoYuan, string HeldCreditNanoYuan, string OutstandingBalanceNanoYuan, string DeletionVersion,
    string? MembershipTier = null);
public sealed record DeleteAccountCommand(string Confirmation, string WechatCode, bool AcknowledgeForfeiture, string DeletionVersion);
public sealed record AccountDeletionResult(bool Deleted);

public sealed class AccountException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;
}

public interface IAccountService
{
    Task<AccountCaptcha> CaptchaAsync(string clientKey, CancellationToken token);
    Task<AccountSessionTokens> LoginAsync(string username, string password, string clientKey, CancellationToken token);
    Task<AccountSessionTokens> WechatLoginAsync(string code, string clientKey, CancellationToken token);
    Task<AccountSessionTokens> RefreshSessionAsync(string refreshToken, CancellationToken token);
    Task LogoutAsync(string refreshToken, CancellationToken token);
    Task<AccountPurchaseRequest> RegisterAsync(RegisterAccountCommand command, string clientKey, CancellationToken token, bool acceptJpeg = false);
    Task<AccountPurchaseRequest> RegistrationAsync(Guid requestId, string proof, bool refresh, CancellationToken token);
    Task<AccountSessionTokens> ClaimAsync(Guid requestId, string proof, CancellationToken token);
    Task<AccountSummary> SummaryAsync(AccountSessionIdentity identity, CancellationToken token);
    Task<AccountTransactions> TransactionsAsync(AccountSessionIdentity identity, string? cursor, int limit, CancellationToken token);
    Task<AccountDeletionEligibility> DeletionEligibilityAsync(AccountSessionIdentity identity, CancellationToken token);
    Task<AccountDeletionResult> DeleteAsync(AccountSessionIdentity identity, DeleteAccountCommand command, CancellationToken token);
    MembershipPackages Packages();
    Task<AccountPurchaseRequest> TopUpAsync(AccountSessionIdentity identity, CancellationToken token, bool acceptJpeg = false);
    Task<AccountPurchaseRequest> MiniProgramPurchaseAsync(AccountSessionIdentity identity, CancellationToken token);
    Task<PublicPurchaseRequest> LinkRegistrationAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token);
    Task<AccountPurchaseRequest> RefreshTopUpAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token);
    Task<AccountPurchaseRequest> CancelTopUpAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token);
    Task<PublicPurchaseRequest> PublicRequestAsync(Guid requestId, AccountSessionIdentity? identity, CancellationToken token);
    Task<AccountPurchaseOrder> CreateOrderAsync(AccountSessionIdentity identity, Guid requestId, string packageId, CancellationToken token,
        Guid? checkoutAttemptId = null);
    Task<AccountOrderStatus> RefreshOrderAsync(AccountSessionIdentity identity, string orderId, CancellationToken token);
    Task ReconcileAsync(CancellationToken token);
    Task<string> VerifyCallbackAsync(string signature, string timestamp, string nonce, string? echo,
        string? encryptedBody, string? messageSignature, CancellationToken token);
}

public sealed record WechatLoginIdentity(string OpenId, string SessionKey);
public sealed record VerifiedVirtualOrder(string OrderId, string OpenId, int PaidFen, string State,
    string? TransactionId, IReadOnlyList<VerifiedVirtualRefund> Refunds, int? OrderFeeFen = null, bool Delivered = false);
public sealed record VerifiedVirtualRefund(string RefundId, int RefundFen, string State);
public interface IWechatAccountPlatform
{
    bool PaymentsAvailable { get; }
    bool ReconciliationAvailable { get; }
    Task<WechatLoginIdentity> LoginAsync(string code, CancellationToken token);
    Task<byte[]> CodeAsync(Guid requestId, CancellationToken token, bool acceptJpeg = false);
    VirtualPaymentParameters Payment(string orderId, string openId, string sessionKey, string packageId, int priceFen);
    VirtualPaymentParameters RefreshPaymentAuthorization(VirtualPaymentParameters parameters, string sessionKey);
    Task<VerifiedVirtualOrder?> QueryAsync(string orderId, string openId, CancellationToken token);
    Task NotifyDeliveryAsync(string orderId, CancellationToken token);
    string VerifyMessage(string signature, string timestamp, string nonce, string? echo,
        string? encryptedBody, string? messageSignature);
}
