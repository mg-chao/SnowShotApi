using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.AccountSerialization;

namespace SnowShot.Infrastructure.Accounts;

/// <summary>Public account API facade. Financial and identity workflows own their state changes.</summary>
internal sealed class PostgresAccountService(PostgresAccountStore store, IWechatAccountPlatform platform,
    WechatAccountOptions options, PostgresAccountSessions sessions, PostgresAccountPurchases purchases,
    PostgresAccountPayments payments, PostgresAccountDeletion deletion) : IAccountService, IAccountSessionResolver
{
    public MembershipPackages Packages() => new(platform.PaymentsAvailable, MembershipCatalog.For(options.AllowPaymentTesting).Select(x =>
        new MembershipPackage(x.Id, x.Fen, Money(checked(x.Fen * 10_000_000L)))).ToArray());

    public async Task<AccountSummary> SummaryAsync(AccountSessionIdentity identity, CancellationToken token)
    {
        if (identity.AccountId is null) return new(false, null, null, "unlinked", "0", "0", "0", platform.PaymentsAvailable);
        await using var db = await store.OpenAsync(token);
        var account = await db.Accounts.SingleOrDefaultAsync(x => x.Id == identity.AccountId && x.Active, token) ?? throw InvalidSession();
        var wallet = await db.AccountWallets.SingleAsync(x => x.AccountId == account.Id, token);
        var freeRecognition = MembershipRules.HasFreeRecognition(wallet.CumulativeRechargeNanoYuan);
        return new(true, account.Id, account.Username, "active", Money(wallet.AvailableNanoYuan), Money(wallet.HeldNanoYuan), Money(wallet.OutstandingNanoYuan), platform.PaymentsAvailable,
            Money(wallet.CumulativeRechargeNanoYuan), MembershipRules.TierFor(wallet.CumulativeRechargeNanoYuan), freeRecognition, freeRecognition, account.IsTemporary);
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1309:Use ordinal string comparison", Justification = "EF translates this comparison using the same PostgreSQL collation as ORDER BY; the StringComparison overload is not translatable.")]
    public async Task<AccountTransactions> TransactionsAsync(AccountSessionIdentity identity, string? cursor, int limit, CancellationToken token)
    {
        if (identity.AccountId is null) return new([], null);
        limit = Math.Clamp(limit, 1, 100);
        await using var db = await store.OpenAsync(token);
        var query = db.AccountFinancialEntries.AsNoTracking().Where(x => x.AccountId == identity.AccountId);
        if (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                var position = JsonSerializer.Deserialize<TransactionCursor>(Convert.FromBase64String(cursor), Json) ?? throw new FormatException();
                query = query.Where(x => x.OccurredAt < position.At || (x.OccurredAt == position.At && string.Compare(x.Id, position.Id) < 0));
            }
            catch (Exception exception) when (exception is FormatException or JsonException)
            { throw new AccountException("invalid_request", "The transaction cursor is invalid."); }
        }
        var entries = await query.OrderByDescending(x => x.OccurredAt).ThenByDescending(x => x.Id).Take(limit + 1).ToListAsync(token);
        var more = entries.Count > limit;
        if (more) entries.RemoveAt(entries.Count - 1);
        var last = entries.LastOrDefault();
        return new(entries.Select(x => new AccountTransaction(x.Id, x.OccurredAt, x.Type, Money(x.AmountNanoYuan), x.Status, x.OrderId, x.Description)).ToArray(),
            more && last is not null ? Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new TransactionCursor(last.OccurredAt, last.Id), Json)) : null);
    }

    public Task<AccountSessionIdentity?> ResolveAsync(string bearerToken, CancellationToken token) => sessions.ResolveAsync(bearerToken, token);
    public Task<AccountCaptcha> CaptchaAsync(string clientKey, CancellationToken token) => sessions.CaptchaAsync(clientKey, token);
    public Task<AccountSessionTokens> LoginAsync(string username, string password, string clientKey, CancellationToken token) => sessions.LoginAsync(username, password, clientKey, token);
    public Task<AccountSessionTokens> WechatLoginAsync(string code, string clientKey, CancellationToken token) => sessions.WechatLoginAsync(code, clientKey, token);
    public Task<AccountSessionTokens> RefreshSessionAsync(string refreshToken, CancellationToken token) => sessions.RefreshSessionAsync(refreshToken, token);
    public Task LogoutAsync(string refreshToken, CancellationToken token) => sessions.LogoutAsync(refreshToken, token);
    public Task<AccountSessionTokens> ClaimAsync(Guid requestId, string proof, CancellationToken token) => sessions.ClaimAsync(requestId, proof, token);
    public Task<AccountPurchaseRequest> RegisterAsync(RegisterAccountCommand command, string clientKey, CancellationToken token) => purchases.RegisterAsync(command, clientKey, token);
    public Task<AccountPurchaseRequest> RegistrationAsync(Guid requestId, string proof, bool refresh, CancellationToken token) => purchases.RegistrationAsync(requestId, proof, refresh, token);
    public Task<AccountPurchaseRequest> TopUpAsync(AccountSessionIdentity identity, CancellationToken token) => purchases.TopUpAsync(identity, token);
    public Task<AccountPurchaseRequest> MiniProgramPurchaseAsync(AccountSessionIdentity identity, CancellationToken token) => purchases.MiniProgramPurchaseAsync(identity, token);
    public Task<PublicPurchaseRequest> LinkRegistrationAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token) => purchases.LinkRegistrationAsync(identity, requestId, token);
    public Task<AccountPurchaseRequest> RefreshTopUpAsync(AccountSessionIdentity identity, Guid requestId, CancellationToken token) => purchases.RefreshTopUpAsync(identity, requestId, token);
    public Task<PublicPurchaseRequest> PublicRequestAsync(Guid requestId, AccountSessionIdentity? identity, CancellationToken token) => purchases.PublicRequestAsync(requestId, identity, token);
    public Task<AccountPurchaseOrder> CreateOrderAsync(AccountSessionIdentity identity, Guid requestId, string packageId, CancellationToken token, Guid? checkoutAttemptId = null) => purchases.CreateOrderAsync(identity, requestId, packageId, token, checkoutAttemptId);
    public Task<AccountOrderStatus> RefreshOrderAsync(AccountSessionIdentity identity, string orderId, CancellationToken token) => payments.RefreshOrderAsync(identity, orderId, token);
    public Task ReconcileAsync(CancellationToken token) => payments.ReconcileAsync(token);
    public Task<string> VerifyCallbackAsync(string signature, string timestamp, string nonce, string? echo, string? encryptedBody, string? messageSignature, CancellationToken token) => payments.VerifyCallbackAsync(signature, timestamp, nonce, echo, encryptedBody, messageSignature, token);
    public Task<AccountDeletionEligibility> DeletionEligibilityAsync(AccountSessionIdentity identity, CancellationToken token) => deletion.DeletionEligibilityAsync(identity, token);
    public Task<AccountDeletionResult> DeleteAsync(AccountSessionIdentity identity, DeleteAccountCommand command, CancellationToken token) => deletion.DeleteAsync(identity, command, token);

    private sealed record TransactionCursor(DateTimeOffset At, string Id);
}
