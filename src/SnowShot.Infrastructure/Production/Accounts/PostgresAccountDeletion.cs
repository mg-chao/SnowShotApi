using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.AccountSerialization;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed class PostgresAccountDeletion(PostgresAccountStore store, IWechatAccountPlatform platform,
    AccountSecrets secrets, PostgresAccountEventInbox inbox)
{
    private const string DeletionConsent = "account-deletion-v1";

    public async Task<AccountDeletionEligibility> DeletionEligibilityAsync(AccountSessionIdentity identity, CancellationToken token)
    {
        await inbox.BackfillRefundRoutingAsync(token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            await RequireLiveSessionAsync(db, identity, now, token, requireWechat: true);
            return (await DeletionStateAsync(db, identity, token)).Eligibility;
        }, token);
    }

    public async Task<AccountDeletionResult> DeleteAsync(AccountSessionIdentity identity, DeleteAccountCommand command, CancellationToken token)
    {
        if (command.Confirmation != "DELETE" || !command.AcknowledgeForfeiture || string.IsNullOrWhiteSpace(command.DeletionVersion))
            throw new AccountException("invalid_confirmation", "Explicitly confirm permanent loss of your account, balance and membership.");
        if (identity.OpenId is null || string.IsNullOrWhiteSpace(command.WechatCode) || command.WechatCode.Length > 512) throw InvalidSession();
        await store.ThrottleAsync("account-delete:" + identity.OpenId, 5, token);
        var verified = await platform.LoginAsync(command.WechatCode, token);
        if (!string.Equals(verified.OpenId, identity.OpenId, StringComparison.Ordinal))
            throw new AccountException("wechat_identity_mismatch", "The current WeChat identity does not match the signed-in account.", 403);

        await inbox.BackfillRefundRoutingAsync(token);
        DateTimeOffset? deletionAt = null;
        return await store.ExecuteAsync(async (db, now) =>
        {
            // Purchases, linking, authentication and refunds use this lock. The
            // wallet row lock below also serializes inference reserve/settle.
            await AccountSynchronization.ChangeIdentityAsync(db, token);
            if (deletionAt is { } committedAt && await db.AccountSessions.AnyAsync(x => x.Id == identity.SessionId &&
                x.RevokedAt == committedAt && x.OpenId == null && x.AccountId == null, token))
                return new AccountDeletionResult(true); // Recover a lost commit acknowledgement.
            await RequireLiveSessionAsync(db, identity, now, token, requireWechat: true);
            var state = await DeletionStateAsync(db, identity, token);
            if (!state.Eligibility.CanDelete)
                throw new AccountException("account_deletion_blocked", "Resolve pending refunds, debt, payments or in-progress usage before deleting the account.", 409);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(command.DeletionVersion),
                Encoding.UTF8.GetBytes(state.Eligibility.DeletionVersion)))
                throw new AccountException("deletion_state_changed", "Your account balance or status changed. Review it and confirm deletion again.", 409);

            deletionAt ??= now;
            var deletedAt = deletionAt.Value;
            var ids = state.Accounts.Select(x => x.Id).ToArray();
            await db.AccountSessions.Where(x => x.OpenId == identity.OpenId || (x.AccountId != null && ids.Contains(x.AccountId.Value)))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.RevokedAt, deletedAt)
                    .SetProperty(x => x.AccessExpiresAt, deletedAt).SetProperty(x => x.RefreshExpiresAt, deletedAt)
                    .SetProperty(x => x.OpenId, (string?)null).SetProperty(x => x.AccountId, (Guid?)null)
                    .SetProperty(x => x.WechatSessionKeyEncrypted, (string?)null), token);
            await db.AccountWechatLinks.Where(x => ids.Contains(x.AccountId)).ExecuteDeleteAsync(token);
            await db.AccountPayerClaims.Where(x => x.OpenId == identity.OpenId || ids.Contains(x.AccountId)).ExecuteDeleteAsync(token);
            await db.AccountUsernameClaims.Where(x => ids.Contains(x.AccountId)).ExecuteDeleteAsync(token);
            // Cancel unissued checkout requests. Issued uncertain orders were
            // rejected above and are never silently abandoned.
            await db.AccountPurchaseRequests.Where(x => ids.Contains(x.AccountId) || x.PayerOpenId == identity.OpenId)
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.State, x => x.OrderId == null && x.State == "awaiting_payment" ? "expired" : x.State)
                    .SetProperty(x => x.PayerOpenId, (string?)null).SetProperty(x => x.ProofHash, (byte[]?)null)
                    .SetProperty(x => x.CodeImage, (byte[]?)null).SetProperty(x => x.BootstrapSessionId, (Guid?)null)
                    .SetProperty(x => x.BootstrapTokensEncrypted, (string?)null), token);
            await db.AccountPaymentOrders.Where(x => ids.Contains(x.AccountId))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.PaymentParametersEncrypted, string.Empty), token);
            foreach (var account in state.Accounts)
            {
                var wallet = state.Wallets.Single(x => x.AccountId == account.Id);
                // Older temporary-account adoptions copied credentials before
                // moving the registration request. Remove that unreferenced
                // inactive copy as well; matching the password hash avoids
                // touching unrelated historical registrations of the same name.
                await EraseOrphanedRegistrationCredentialsAsync(db, account, deletedAt, DeletionConsent, token);
                account.Active = false;
                account.IsTemporary = false;
                account.Username = "deleted_" + Guid.NewGuid().ToString("N")[..24];
                account.NormalizedUsername = account.Username.ToUpperInvariant();
                account.PasswordHash = string.Empty;
                account.ActivatedAt = null;
                account.DeletedAt = deletedAt;
                account.DeletionConsentVersion = DeletionConsent;
                account.ForfeitedCreditNanoYuan = wallet.AvailableNanoYuan;
                if (wallet.AvailableNanoYuan > 0)
                    db.AccountFinancialEntries.Add(new()
                    {
                        Id = "deletion:" + account.Id.ToString("N"),
                        AccountId = account.Id,
                        Type = "forfeiture",
                        AmountNanoYuan = -wallet.AvailableNanoYuan,
                        Status = "completed",
                        OccurredAt = deletedAt,
                        Description = "Account deletion: explicitly acknowledged balance forfeiture"
                    });
                wallet.AvailableNanoYuan = 0;
                wallet.CumulativeRechargeNanoYuan = 0;
            }
            return new AccountDeletionResult(true);
        }, token);
    }


    private async Task<DeletionState> DeletionStateAsync(SnowShotDbContext db, AccountSessionIdentity identity, CancellationToken token)
    {
        var linkedId = await db.AccountWechatLinks.Where(x => x.OpenId == identity.OpenId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
        var pendingIds = await db.AccountPayerClaims.Where(x => x.OpenId == identity.OpenId).Select(x => x.AccountId).ToListAsync(token);
        var directIds = await db.AccountPurchaseRequests.Where(x => x.PayerOpenId == identity.OpenId && x.Purpose == "mini_registration")
            .Select(x => x.AccountId).ToListAsync(token);
        var ids = pendingIds.Concat(directIds).Concat(linkedId is { } id ? [id] : []).Distinct().Order().ToArray();
        var accounts = await db.Accounts.Where(x => ids.Contains(x.Id) && x.DeletedAt == null).OrderBy(x => x.Id).ToListAsync(token);
        ids = accounts.Select(x => x.Id).ToArray();
        var wallets = new List<AccountWalletEntity>();
        foreach (var accountId in ids)
            wallets.Add(await WalletAsync(db, accountId, token));
        var orders = await db.AccountPaymentOrders.Where(x => ids.Contains(x.AccountId) || x.OpenId == identity.OpenId).OrderBy(x => x.Id).ToListAsync(token);
        var orderIds = orders.Select(x => x.Id).ToArray();
        var refunds = await db.AccountRefunds.Where(x => orderIds.Contains(x.OrderId)).OrderBy(x => x.Id).ToListAsync(token);
        var pendingRefundEvent = await db.AccountPlatformEvents.AnyAsync(x => x.EventName == "xpay_refund_notify" &&
            x.State != "processed" && orderIds.Contains(x.MerchantOrderId!), token);
        // Legacy evidence is indexed in bounded batches outside identity/wallet
        // locks. Until all unresolved routing is known, deletion must fail closed.
        var unknownRefundEvent = await db.AccountPlatformEvents.AnyAsync(x => x.EventName == "xpay_refund_notify" &&
            x.State != "processed" && !x.RoutingMetadataComplete, token);
        var blockers = new List<string>();
        if (pendingRefundEvent || unknownRefundEvent || refunds.Any(x => x.State == "processing" || (x.State == "completed" && !x.Applied))) blockers.Add("pending_refund");
        if (wallets.Any(x => x.OutstandingNanoYuan > 0)) blockers.Add("outstanding_balance");
        // Financial history from an already deleted account must not keep a new
        // WeChat registration tied to that account or block it forever.
        if (orders.Any(x => (x.State != "closed" && x.State != "failed") &&
            (x.State != "paid" || !x.Fulfilled || !x.DeliveryConfirmed))) blockers.Add("pending_payment");
        if (wallets.Any(x => x.HeldNanoYuan > 0) || await db.UsageOperations.AnyAsync(x => x.AccountId != null && ids.Contains(x.AccountId.Value) &&
            (x.State == ReservationState.Reserved || x.State == ReservationState.Dispatched), token)) blockers.Add("active_usage");
        var version = secrets.Fingerprint(DeletionConsent + ":" + identity.SessionId.ToString("N") + ":" + JsonSerializer.Serialize(new
        {
            linkedId,
            accounts = accounts.Select(x => new { x.Id, x.Active, x.DeletedAt }),
            wallets = wallets.Select(x => new { x.AccountId, x.AvailableNanoYuan, x.HeldNanoYuan, x.OutstandingNanoYuan, x.CumulativeRechargeNanoYuan }),
            orders = orders.Select(x => new { x.Id, x.State, x.PaidFen, x.Fulfilled, x.DeliveryConfirmed }),
            refunds = refunds.Select(x => new { x.Id, x.State, x.Applied, x.RefundFen }),
            blockers
        }, Json));
        var linkedWallet = wallets.SingleOrDefault(x => x.AccountId == linkedId);
        var tier = linkedWallet is null ? null : MembershipRules.TierFor(linkedWallet.CumulativeRechargeNanoYuan);
        return new(accounts, wallets, new(blockers.Count == 0, blockers, Money(wallets.Sum(x => x.AvailableNanoYuan)),
            Money(wallets.Sum(x => x.HeldNanoYuan)), Money(wallets.Sum(x => x.OutstandingNanoYuan)), version, tier));
    }

    private sealed record DeletionState(List<AccountEntity> Accounts, List<AccountWalletEntity> Wallets, AccountDeletionEligibility Eligibility);
}
