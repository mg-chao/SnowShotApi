using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class PostgresAccountPayments(PostgresAccountStore store, IWechatAccountPlatform platform,
    AccountSecrets secrets, TimeProvider clock, PostgresPurchaseRequests requests, PostgresAccountEventInbox inbox,
    PostgresAccountRetryQueue retryQueue, ILogger<PostgresAccountPayments> logger)
{
    public async Task<AccountOrderStatus> RefreshOrderAsync(AccountSessionIdentity identity, string orderId, CancellationToken token)
    {
        await using (var db = await store.OpenAsync(token))
            if (!await db.AccountPaymentOrders.AnyAsync(x => x.Id == orderId && x.OpenId == identity.OpenId, token)) throw NotFound();
        await RefreshPaymentAsync(orderId, token);
        await using var read = await store.OpenAsync(token);
        var order = await read.AccountPaymentOrders.SingleAsync(x => x.Id == orderId, token);
        if (order.State == AccountPurchaseLifecycle.Refunded) return new(order.Id, order.RequestId, order.State, "completed");
        var refunds = await read.AccountRefunds.Where(x => x.OrderId == orderId).Select(x => x.State).ToListAsync(token);
        var refundState = refunds.Contains("processing", StringComparer.Ordinal) ? "processing" :
            refunds.Contains("failed", StringComparer.Ordinal) ? "failed" : refunds.Count > 0 ? "completed" : null;
        return new(order.Id, order.RequestId, await AccountPurchaseLifecycle.ReportedOrderStateAsync(read, order, token), refundState);
    }
    internal async Task RefreshRequestPaymentAsync(Guid requestId, CancellationToken token)
    {
        await using var db = await store.OpenAsync(token);
        var id = await db.AccountPurchaseRequests.Where(x => x.Id == requestId).Select(x => x.OrderId).SingleOrDefaultAsync(token);
        if (id is not null) await RefreshPaymentAsync(id, token);
    }
    private async Task RefreshPaymentAsync(string orderId, CancellationToken token)
    {
        AccountPaymentOrderEntity? order;
        await using (var db = await store.OpenAsync(token))
        {
            order = await db.AccountPaymentOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, token);
            if (order is null || order.State == AccountPurchaseLifecycle.Refunded) return;
            if (!order.Fulfilled && (await AccountPurchaseLifecycle.RequiresClosureReconciliationAsync(db, order, token) ||
                 (order.PaidFen is > 0 && await CompletedRefundAmountAsync(db, orderId, token) == order.PaidFen)) &&
                await ReconcileRetainedPaymentAsync(order, token)) return;
        }
        if (!platform.ReconciliationAvailable) return;
        await using (var update = await store.OpenAsync(token))
        {
            var now = clock.GetUtcNow(); var before = now.AddSeconds(-3);
            if (await update.AccountPaymentOrders.Where(x => x.Id == orderId && (x.LastQueriedAt == null || x.LastQueriedAt <= before))
                .ExecuteUpdateAsync(set => set.SetProperty(x => x.LastQueriedAt, now), token) == 0) return;
        }
        var verified = await platform.QueryAsync(order.Id, order.OpenId, token);
        if (verified is not null)
        {
            await ApplyPaymentAsync(verified, token);
            await NotifyDeliveryAsync(orderId, token);
        }
    }
    private async Task<bool> ApplyPaymentAsync(VerifiedVirtualOrder verified, CancellationToken token)
    {
        bool preservesIdentity;
        await using (var read = await store.OpenAsync(token))
            preservesIdentity = await (from value in read.AccountPaymentOrders
                                       join purchase in read.AccountPurchaseRequests on value.RequestId equals purchase.Id
                                       where value.Id == verified.OrderId
                                       select value.Fulfilled || purchase.Purpose == "top_up").SingleOrDefaultAsync(token);
        return await store.ExecuteAsync(async (db, now) =>
        {
            if (preservesIdentity) await AccountSynchronization.ReadIdentityAsync(db, token);
            else await AccountSynchronization.ChangeIdentityAsync(db, token);
            var accountId = await db.AccountPaymentOrders.Where(x => x.Id == verified.OrderId).Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
            if (accountId is null) return false;
            await AccountSynchronization.AccountAsync(db, accountId.Value, token);
            if (!string.IsNullOrWhiteSpace(verified.TransactionId))
                await AccountSynchronization.EvidenceAsync(db, "platform-payment", verified.TransactionId, token);
            var order = await db.AccountPaymentOrders.SingleOrDefaultAsync(x => x.Id == verified.OrderId, token);
            if (order is null) return false;
            if (order.State == AccountPurchaseLifecycle.Refunded) return false;
            if (!preservesIdentity && !order.Fulfilled && verified.State is ("paid" or "closed"))
                await PostgresPurchaseRequests.ExpireRequestsAsync(db, token, order.RequestId,
                    verified.State == "paid" ? order.OpenId : null);
            var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == order.RequestId, token);
            var account = await db.Accounts.SingleAsync(x => x.Id == order.AccountId, token);
            if (account.DeletedAt is not null) return order.Fulfilled && order.State == "paid";
            if (request.Purpose == "top_up" && !account.Active)
            { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
            if (verified.OpenId != order.OpenId) { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
            // Once the platform has observed the order, recovery is query-only.
            // An absent result deliberately does not establish nonacceptance.
            order.VerifiedAt = now;
            if (verified.State == "closed" && !order.Fulfilled)
            {
                // Retain a contradictory closure separately from verified payment evidence.
                order.ClosedWithPositivePaidFee |= verified.PaidFen > 0;
                if (await AccountPurchaseLifecycle.HasEvidenceAgainstUnpaidClosureAsync(db, order, token))
                {
                    await RetainPaymentForReconciliationAsync(db, order, request, token);
                    return false;
                }
                AccountPurchaseLifecycle.CloseUnfulfilled(order, request);
                await db.AccountPayerClaims.Where(x => x.RequestId == request.Id).ExecuteDeleteAsync(token);
                if (!account.Active) await db.AccountUsernameClaims.Where(x => x.RequestId == request.Id).ExecuteDeleteAsync(token);
                var entry = await db.AccountFinancialEntries.SingleAsync(x => x.Id == "topup:" + order.Id, token); entry.Status = "failed";
                return true;
            }
            if (verified.State != "paid") return false;
            if (!MatchesPaymentEvidence(order, verified))
            { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
            if (await db.AccountPaymentOrders.AnyAsync(x => x.Id != order.Id && x.PlatformTransactionId == verified.TransactionId, token))
            { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
            order.PaidFen = verified.PaidFen; order.PlatformTransactionId = verified.TransactionId; order.VerifiedAt = now;
            var refundedFen = await CompletedRefundAmountAsync(db, order.Id, token);
            if (refundedFen > verified.PaidFen ||
                (!order.Fulfilled && refundedFen > 0 && !await UnappliedRefundsMatchPaymentAsync(db, order, token)))
            { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
            if (!order.Fulfilled && refundedFen == verified.PaidFen && AccountPurchaseLifecycle.IsExpiredRegistration(request))
            {
                await CompleteRefundedRegistrationAsync(db, order, request, verified.PaidFen, now, token);
                return false;
            }
            order.State = "payment_pending";
            if (!order.Fulfilled)
            {
                var expiredRegistration = AccountPurchaseLifecycle.IsExpiredRegistration(request);
                var usernameClaims = db.AccountUsernameClaims.AsNoTracking().Where(x => x.NormalizedUsername == account.NormalizedUsername);
                var claim = await usernameClaims.SingleOrDefaultAsync(token);
                if (expiredRegistration && claim is not null && claim.AccountId != account.Id)
                {
                    await PostgresPurchaseRequests.ExpireRequestsAsync(db, token, claim.RequestId);
                    claim = await usernameClaims.SingleOrDefaultAsync(token);
                }
                var usernameTaken = claim is not null && claim.AccountId != account.Id;
                var useTemporaryAccount = !account.Active && (request.Purpose == "mini_registration" ||
                    (expiredRegistration && (usernameTaken || account.PasswordHash.Length == 0 || request.ProofHash is null)));
                var links = await db.AccountWechatLinks.Where(x => x.OpenId == order.OpenId || x.AccountId == account.Id).ToListAsync(token);
                var provisional = await db.AccountPayerClaims.Where(x => x.OpenId == order.OpenId || x.AccountId == account.Id).ToListAsync(token);
                // Top-ups keep identity unchanged. Missing established claims or
                // links require reconciliation, never promotion under a shared gate.
                if (request.Purpose == "top_up" && (claim?.AccountId != account.Id || links.Count != 1))
                { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
                if ((usernameTaken && !(expiredRegistration && useTemporaryAccount)) || links.Any(x => x.OpenId != order.OpenId || x.AccountId != account.Id) ||
                    provisional.Any(x => x.OpenId != order.OpenId || x.AccountId != account.Id))
                { AccountPurchaseLifecycle.RequireReconciliation(order, request); return false; }
                if (useTemporaryAccount)
                {
                    await db.AccountUsernameClaims.Where(x => x.AccountId == account.Id).ExecuteDeleteAsync(token);
                    account.Username = await PostgresPurchaseRequests.UnusedUsernameAsync(db, "wx_", token);
                    account.NormalizedUsername = AccountRules.NormalizeUsername(account.Username);
                    account.PasswordHash = string.Empty;
                    account.IsTemporary = true;
                    claim = null;
                }
                if (claim is null) db.AccountUsernameClaims.Add(new() { NormalizedUsername = account.NormalizedUsername, AccountId = account.Id, RequestId = request.Id });
                if (links.Count == 0) db.AccountWechatLinks.Add(new() { AccountId = account.Id, OpenId = order.OpenId, CreatedAt = now });
                var wallet = await WalletAsync(db, account.Id, token);
                wallet.CumulativeRechargeNanoYuan = checked(wallet.CumulativeRechargeNanoYuan + verified.PaidFen * MembershipRules.NanoYuanPerFen);
                (wallet.AvailableNanoYuan, wallet.OutstandingNanoYuan) = AccountRules.AddFunds(wallet.AvailableNanoYuan, wallet.OutstandingNanoYuan, checked(verified.PaidFen * 10_000_000L));
                var entry = await db.AccountFinancialEntries.SingleAsync(x => x.Id == "topup:" + order.Id, token);
                entry.AmountNanoYuan = checked(verified.PaidFen * 10_000_000L); entry.Status = "completed"; entry.OccurredAt = now;
                account.Active = true; account.ActivatedAt ??= now; order.Fulfilled = true;
                if (!expiredRegistration || !useTemporaryAccount)
                {
                    request.FulfilledAt = now;
                    request.CompletionKind = "payment";
                }
                await db.AccountPayerClaims.Where(x => x.RequestId == request.Id).ExecuteDeleteAsync(token);
                await ApplyRefundsAsync(db, order, request, wallet, now, token);
            }
            if (order.State != "reconciliation_required")
            {
                AccountPurchaseLifecycle.CompletePayment(order, request, verified.Delivered);
            }
            return true;
        }, token);
    }

    private static bool MatchesPaymentEvidence(AccountPaymentOrderEntity order, VerifiedVirtualOrder payment) =>
        payment.State == "paid" && payment.OrderId == order.Id && payment.OpenId == order.OpenId &&
        payment.PaidFen > 0 && payment.PaidFen <= order.PriceFen &&
        (payment.OrderFeeFen is null || payment.OrderFeeFen == order.PriceFen) &&
        !string.IsNullOrWhiteSpace(payment.TransactionId) &&
        (order.PlatformTransactionId is null || order.PlatformTransactionId == payment.TransactionId) &&
        (order.PaidFen is null || order.PaidFen == payment.PaidFen);

    private async Task NotifyDeliveryAsync(string orderId, CancellationToken token)
    {
        var lease = await retryQueue.ClaimAsync(AccountRetryKind.Delivery, orderId, token);
        if (lease is not null) await ExecuteRetryAsync(lease, token);
    }

    private Task<bool> ReconcileRetainedPaymentAsync(AccountPaymentOrderEntity observed, CancellationToken token) =>
        store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            await AccountSynchronization.AccountAsync(db, observed.AccountId, token);
            var order = await db.AccountPaymentOrders.SingleAsync(x => x.Id == observed.Id, token);
            if (order.State == AccountPurchaseLifecycle.Refunded) return true;
            if (order.Fulfilled) return false;
            var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == order.RequestId, token);
            if (order.PaidFen is > 0 and var paidFen && !string.IsNullOrWhiteSpace(order.PlatformTransactionId) &&
                AccountPurchaseLifecycle.IsExpiredRegistration(request) &&
                await CompletedRefundAmountAsync(db, order.Id, token) == paidFen &&
                await UnappliedRefundsMatchPaymentAsync(db, order, token))
            {
                await CompleteRefundedRegistrationAsync(db, order, request, paidFen, now, token);
                return true;
            }
            if (await AccountPurchaseLifecycle.RequiresClosureReconciliationAsync(db, order, token))
                await RetainPaymentForReconciliationAsync(db, order, request, token);
            return false;
        }, token);

    private static async Task RetainPaymentForReconciliationAsync(SnowShotDbContext db, AccountPaymentOrderEntity order,
        AccountPurchaseRequestEntity request, CancellationToken token)
    {
        AccountPurchaseLifecycle.RequireReconciliation(order, request);
        var entry = await db.AccountFinancialEntries.SingleAsync(x => x.Id == "topup:" + order.Id, token);
        entry.Status = "pending";
    }

    private static Task<long> CompletedRefundAmountAsync(SnowShotDbContext db, string orderId, CancellationToken token) =>
        db.AccountRefunds.Where(x => x.OrderId == orderId && x.State == "completed").SumAsync(x => (long)x.RefundFen, token);

    private static async Task CompleteRefundedRegistrationAsync(SnowShotDbContext db, AccountPaymentOrderEntity order,
        AccountPurchaseRequestEntity request, int paidFen, DateTimeOffset now, CancellationToken token)
    {
        AccountPurchaseLifecycle.CompleteRefundedRegistration(order, request);
        // The full verified refund offsets the payment without ever crediting or debiting a wallet.
        await db.AccountRefunds.Where(x => x.OrderId == order.Id && x.State == "completed" && !x.Applied)
            .ExecuteUpdateAsync(set => set.SetProperty(x => x.Applied, true).SetProperty(x => x.UpdatedAt, now), token);
        var entry = await db.AccountFinancialEntries.SingleAsync(x => x.Id == "topup:" + order.Id, token);
        entry.AmountNanoYuan = checked(paidFen * MembershipRules.NanoYuanPerFen);
        entry.Status = "completed";
        entry.OccurredAt = now;
    }

    private static async Task ApplyRefundsAsync(SnowShotDbContext db, AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request, AccountWalletEntity wallet, DateTimeOffset now, CancellationToken token)
    {
        var refunds = await db.AccountRefunds.Where(x => x.OrderId == order.Id).OrderBy(x => x.Id).ToListAsync(token);
        if (refunds.Where(x => x.State == "completed").Sum(x => (long)x.RefundFen) > order.PaidFen)
        { AccountPurchaseLifecycle.RequireReconciliation(order, request); return; }
        foreach (var refund in refunds.Where(x => x.State == "completed" && !x.Applied))
        {
            ApplyRefund(wallet, refund, now);
        }
    }

    private static void ApplyRefund(AccountWalletEntity wallet, AccountRefundEntity refund, DateTimeOffset now)
    {
        var amount = checked(refund.RefundFen * MembershipRules.NanoYuanPerFen);
        wallet.CumulativeRechargeNanoYuan = checked(wallet.CumulativeRechargeNanoYuan - amount);
        (wallet.AvailableNanoYuan, wallet.OutstandingNanoYuan) = AccountRules.Refund(wallet.AvailableNanoYuan,
            wallet.OutstandingNanoYuan, amount);
        refund.Applied = true;
        refund.UpdatedAt = now;
    }
}
