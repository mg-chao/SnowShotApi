using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SnowShot.Application;
using SnowShot.Infrastructure.Persistence;
using static SnowShot.Infrastructure.Accounts.AccountErrors;
using static SnowShot.Infrastructure.Accounts.AccountSerialization;
using static SnowShot.Infrastructure.Accounts.PostgresAccountStore;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class PostgresAccountPayments
{
    public async Task<string> VerifyCallbackAsync(string signature, string timestamp, string nonce, string? echo,
        string? encryptedBody, string? messageSignature, CancellationToken token)
    {
        var payload = platform.VerifyMessage(signature, timestamp, nonce, echo, encryptedBody, messageSignature);
        if (encryptedBody is null) return payload;
        var fields = WechatPlatformMessage.Parse(payload);
        var name = Field(fields, "Event") ?? string.Empty;
        if (name.Length > 128) throw new AccountException("invalid_platform_message", "The platform event is malformed.");
        var id = Convert.ToHexStringLower(AccountSecrets.Hash(payload));
        var encrypted = secrets.Protect(payload);
        var merchantOrderId = PostgresAccountEventInbox.MerchantOrderId(fields, name);
        await store.ExecuteAsync(async (inbox, _) =>
        {
            await AccountSynchronization.ReadIdentityAsync(inbox, token);
            await inbox.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO snowshot.account_platform_events ("Id","EventName","MerchantOrderId","RoutingMetadataComplete","PayloadEncrypted","State","ReceivedAt","Attempts")
                VALUES ({id},{name},{merchantOrderId},TRUE,{encrypted},'pending',clock_timestamp(),0)
                ON CONFLICT ("Id") DO UPDATE SET "MerchantOrderId"=EXCLUDED."MerchantOrderId","RoutingMetadataComplete"=TRUE
                """, token);
            return true;
        }, token);
        await using var db = await store.OpenAsync(token);
        if (name == "xpay_goods_deliver_notify")
        {
            // A delivery success response means the goods have actually been
            // granted. The retained inbox also recovers interrupted callbacks.
            await ProcessEventAsync(new() { Id = id, EventName = name, PayloadEncrypted = encrypted, State = "pending" }, token, true);
            await db.AccountPlatformEvents.Where(x => x.Id == id).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.State, "processed").SetProperty(x => x.ProcessedAt, clock.GetUtcNow())
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), token);
        }
        return fields.SuccessReply;
    }

    public async Task ReconcileAsync(CancellationToken token)
    {
        await inbox.BackfillRefundRoutingAsync(token);
        await requests.ExpireDueRequestsAsync(null, token);
        for (var index = 0; index < 25; index++)
        {
            AccountPlatformEventEntity? entry;
            await using (var db = await store.OpenAsync(token))
                entry = (await db.AccountPlatformEvents.FromSqlRaw("""
                    WITH candidate AS (SELECT "Id" FROM snowshot.account_platform_events
                    WHERE ("State"='pending' AND ("LeaseExpiresAt" IS NULL OR "LeaseExpiresAt"<=clock_timestamp()))
                       OR ("State"='processing' AND "LeaseExpiresAt"<=clock_timestamp())
                    ORDER BY "Attempts","ReceivedAt" FOR UPDATE SKIP LOCKED LIMIT 1)
                    UPDATE snowshot.account_platform_events SET "State"='processing',"LeaseExpiresAt"=clock_timestamp()+interval '2 minutes',"Attempts"="Attempts"+1
                    WHERE "Id" IN (SELECT "Id" FROM candidate) RETURNING *
                    """).AsNoTracking().ToListAsync(token)).SingleOrDefault();
            if (entry is null) break;
            try
            {
                await ProcessEventAsync(entry, token);
                await using var db = await store.OpenAsync(token);
                await db.AccountPlatformEvents.Where(x => x.Id == entry.Id && x.LeaseExpiresAt == entry.LeaseExpiresAt).ExecuteUpdateAsync(set =>
                    set.SetProperty(x => x.State, "processed").SetProperty(x => x.ProcessedAt, clock.GetUtcNow()).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), token);
            }
            catch (Exception exception) when (exception is AccountException or HttpRequestException or TaskCanceledException or CryptographicException or FormatException)
            {
                token.ThrowIfCancellationRequested();
                await using var db = await store.OpenAsync(token);
                await db.AccountPlatformEvents.Where(x => x.Id == entry.Id && x.LeaseExpiresAt == entry.LeaseExpiresAt).ExecuteUpdateAsync(set =>
                    set.SetProperty(x => x.State, "pending").SetProperty(x => x.LeaseExpiresAt, clock.GetUtcNow().AddMinutes(1)), token);
            }
        }
        if (platform.ReconciliationAvailable)
        {
            foreach (var kind in new[] { AccountRetryKind.Delivery, AccountRetryKind.Refund })
            {
                for (var index = 0; index < 20; index++)
                {
                    var lease = await retryQueue.ClaimAsync(kind, null, token);
                    if (lease is null) break;
                    await ExecuteRetryAsync(lease, token);
                }
            }
            List<string> orders;
            var before = clock.GetUtcNow().AddMinutes(-5);
            await using (var db = await store.OpenAsync(token))
                orders = await db.AccountPaymentOrders.Where(x => (x.State == "payment_pending" || x.State == "reconciliation_required") &&
                    (x.LastQueriedAt == null || x.LastQueriedAt < before)).OrderBy(x => x.LastQueriedAt).ThenBy(x => x.CreatedAt).Select(x => x.Id).Take(20).ToListAsync(token);
            foreach (var id in orders)
            {
                try { await RefreshPaymentAsync(id, token); }
                catch (Exception exception) when (exception is AccountException or HttpRequestException or TaskCanceledException) { token.ThrowIfCancellationRequested(); }
            }
        }
        await using var cleanup = await store.OpenAsync(token);
        var expiredBefore = clock.GetUtcNow().AddDays(-1);
        await cleanup.AccountCaptchas.Where(x => x.ExpiresAt < expiredBefore).ExecuteDeleteAsync(token);
        await cleanup.AccountThrottles.Where(x => x.PeriodStart < expiredBefore).ExecuteDeleteAsync(token);
        // Sessions, requests, platform evidence, orders and financial entries are never swept here.
    }

    private async Task ExecuteRetryAsync(AccountRetryLease lease, CancellationToken token)
    {
        try
        {
            await using var db = await store.OpenAsync(token);
            if (lease.Kind == AccountRetryKind.Delivery)
            {
                await platform.NotifyDeliveryAsync(lease.Id, token);
                await db.AccountPaymentOrders.Where(x => x.Id == lease.Id && x.Fulfilled && x.DeliveryLeaseToken == lease.Token)
                    .ExecuteUpdateAsync(set => set.SetProperty(x => x.DeliveryConfirmed, true), token);
            }
            else
            {
                var refund = await db.AccountRefunds.AsNoTracking().SingleAsync(x => x.Id == lease.Id, token);
                var openId = await db.AccountPaymentOrders.Where(x => x.Id == refund.OrderId).Select(x => x.OpenId).SingleAsync(token);
                var result = await platform.QueryAsync(refund.Id, openId, token);
                var verified = result?.Refunds.SingleOrDefault(x => x.RefundId == refund.Id);
                if (verified is null || result!.TransactionId != refund.PlatformRefundId)
                    throw new AccountException("reconciliation_required", "The retained refund has no matching platform result.", 409);
                await RecordRefundAsync(refund.OrderId, refund.Id, refund.PlatformRefundId, verified.RefundFen,
                    verified.State == "completed" && refund.SuccessNotified ? "completed" : verified.State == "failed" ? "failed" : "processing",
                    refund.SuccessNotified, token, lease);
            }
        }
        catch (Exception error) when (error is AccountException or HttpRequestException or TaskCanceledException)
        {
            token.ThrowIfCancellationRequested();
            RetryDeferred(logger, lease.Kind.ToString(), lease.Id, lease.Attempt,
                error is AccountException account ? account.Code : error.GetType().Name);
        }
        // Cancellation or an unexpected failure leaves a lease that expires. No
        // request can immediately hot-loop this record or discard its evidence.
        await retryQueue.ReleaseAsync(lease, token);
    }

    [LoggerMessage(Level = LogLevel.Warning, EventId = 5504,
        Message = "Account {Kind} reconciliation deferred for {RecordId} after attempt {Attempt}: {Reason}")]
    private static partial void RetryDeferred(ILogger logger, string kind, string recordId, int attempt, string reason);

    private async Task ProcessEventAsync(AccountPlatformEventEntity entry, CancellationToken token, bool acknowledgeDelivery = false)
    {
        var fields = WechatPlatformMessage.Parse(secrets.Unprotect(entry.PayloadEncrypted));
        var orderId = Field(fields, entry.EventName == "xpay_refund_notify" ? "MchOrderId" : "OutTradeNo");
        if (entry.EventName is not ("xpay_goods_deliver_notify" or "xpay_refund_notify")) return;
        if (string.IsNullOrWhiteSpace(orderId)) throw new AccountException("reconciliation_required", "The payment event has no merchant order.", 409);
        AccountPaymentOrderEntity order;
        await using (var db = await store.OpenAsync(token))
            order = await db.AccountPaymentOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, token) ?? throw NotFound();
        if (Field(fields, "OpenId") != order.OpenId) throw LinkConflict();
        if (entry.EventName == "xpay_goods_deliver_notify")
        {
            await using (var deleted = await store.OpenAsync(token))
                if (await deleted.Accounts.AnyAsync(x => x.Id == order.AccountId && x.DeletedAt != null, token))
                {
                    if (order.Fulfilled && order.State == "paid") return;
                    throw new AccountException("reconciliation_required", "A payment for a deleted account requires manual reconciliation.", 409);
                }
            var parameters = JsonSerializer.Deserialize<VirtualPaymentParameters>(secrets.Unprotect(order.PaymentParametersEncrypted), Json)
                ?? throw new AccountException("reconciliation_required", "The original signed order is unavailable.", 409);
            var queried = string.IsNullOrWhiteSpace(fields["WeChatPayInfo.MchOrderNo"])
                ? await platform.QueryAsync(order.Id, order.OpenId, token) : null;
            var delivery = fields.Delivery(order.Id, order.OpenId, order.PriceFen, parameters, queried, acknowledgeDelivery);
            if (!await ApplyPaymentAsync(delivery, token))
                throw new AccountException("reconciliation_required", "The delivery requires payment reconciliation.", 409);
            if (!acknowledgeDelivery) await NotifyDeliveryAsync(order.Id, token);
            return;
        }
        if (entry.EventName == "xpay_refund_notify")
        {
            var refundId = Field(fields, "MchRefundId");
            var wxRefundId = Field(fields, "WxRefundId");
            if (string.IsNullOrWhiteSpace(refundId) || string.IsNullOrWhiteSpace(wxRefundId))
                throw new AccountException("reconciliation_required", "The refund identifiers are missing.", 409);
            var query = await platform.QueryAsync(refundId, order.OpenId, token);
            var refund = query?.Refunds.SingleOrDefault(x => x.RefundId == refundId);
            if (refund is null || refund.RefundFen <= 0) throw new AccountException("reconciliation_required", "The refund result is not yet available.", 409);
            var retCode = Field(fields, "RetCode");
            var payment = await platform.QueryAsync(order.Id, order.OpenId, token);
            if (retCode is null || query!.TransactionId != wxRefundId || payment?.TransactionId != Field(fields, "WxOrderId") ||
                !int.TryParse(Field(fields, "RefundFee"), NumberStyles.None, CultureInfo.InvariantCulture, out var notificationFen) || notificationFen != refund.RefundFen)
                throw new AccountException("reconciliation_required", "The refund notification identifiers or amount could not be verified.", 409);
            var state = retCode == "0" && refund.State == "completed" ? "completed" : retCode != "0" && refund.State != "completed" ? "failed" : "processing";
            await RecordRefundAsync(order.Id, refundId, wxRefundId, refund.RefundFen, state, retCode == "0", token);
        }
        await RefreshPaymentAsync(order.Id, token);
    }
    private async Task RecordRefundAsync(string orderId, string refundId, string platformId, int amountFen, string state, bool successNotified, CancellationToken token, AccountRetryLease? lease = null)
    {
        await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            var accountId = await db.AccountPaymentOrders.Where(x => x.Id == orderId).Select(x => x.AccountId).SingleAsync(token);
            await AccountSynchronization.AccountAsync(db, accountId, token);
            await AccountSynchronization.EvidenceAsync(db, "refund", refundId, token);
            await AccountSynchronization.EvidenceAsync(db, "platform-refund", platformId, token);
            var order = await db.AccountPaymentOrders.SingleAsync(x => x.Id == orderId, token);
            var previous = await db.AccountRefunds.FromSqlInterpolated($"SELECT * FROM snowshot.account_refunds WHERE \"Id\"={refundId} FOR UPDATE").SingleOrDefaultAsync(token);
            if (lease is not null && previous?.QueryLeaseToken != lease.Token) return false;
            if (await db.AccountRefunds.AnyAsync(x => x.Id != refundId && x.PlatformRefundId == platformId, token))
                throw new AccountException("reconciliation_required", "The platform refund is already associated with another retained refund.", 409);
            if (previous is not null && (previous.OrderId != orderId || previous.RefundFen != amountFen || previous.PlatformRefundId != platformId))
                throw new AccountException("reconciliation_required", "The refund conflicts with retained financial evidence.", 409);
            if (previous?.State == "completed") return true;
            var otherCompleted = await db.AccountRefunds.Where(x => x.OrderId == orderId && x.Id != refundId && x.State == "completed").SumAsync(x => (long)x.RefundFen, token);
            if (amountFen <= 0 || otherCompleted + amountFen > (order.PaidFen ?? order.PriceFen))
                throw new AccountException("reconciliation_required", "The refund exceeds the verified payment amount.", 409);
            var refund = previous ?? new AccountRefundEntity { Id = refundId, OrderId = orderId, RefundFen = amountFen, State = state, PlatformRefundId = platformId };
            if (previous is null) db.AccountRefunds.Add(refund);
            refund.State = state; refund.UpdatedAt = now;
            refund.SuccessNotified |= successNotified;
            var effectId = "refund:" + refundId;
            var entry = await db.AccountFinancialEntries.SingleOrDefaultAsync(x => x.Id == effectId, token);
            if (entry is null)
            {
                entry = new()
                {
                    Id = effectId,
                    AccountId = order.AccountId,
                    Type = "refund",
                    Status = "pending",
                    AmountNanoYuan = checked(-amountFen * 10_000_000L),
                    OrderId = orderId,
                    OccurredAt = now,
                    Description = "Platform refund"
                }; db.AccountFinancialEntries.Add(entry);
            }
            entry.Status = state == "completed" ? "completed" : state == "failed" ? "failed" : "pending";
            entry.OccurredAt = now;
            if (state == "completed" && order.Fulfilled && !refund.Applied)
            {
                if (await db.Accounts.AnyAsync(x => x.Id == order.AccountId && x.DeletedAt != null, token))
                    refund.Applied = true; // Retain the refund evidence without recreating deleted rights or debt.
                else
                {
                    var wallet = await WalletAsync(db, order.AccountId, token);
                    ApplyRefund(wallet, refund, now);
                }
            }
            return true;
        }, token);
    }
    private static string? Field(WechatPlatformMessage fields, string key) => fields[key];
}
