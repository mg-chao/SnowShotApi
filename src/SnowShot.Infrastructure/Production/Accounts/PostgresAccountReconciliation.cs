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
        var refundNotification = name == "xpay_refund_notify" ? ReadRefundNotification(fields) : null;
        await store.ExecuteAsync(async (inbox, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(inbox, token);
            var alreadyApplied = refundNotification is not null && await IsAppliedRefundAsync(inbox, refundNotification, token);
            if (!alreadyApplied && refundNotification is { SuccessNotified: true } &&
                await TryRequeueUnsettledRefundNotificationAsync(inbox, refundNotification, id, encrypted, token))
                return true;
            var state = alreadyApplied ? "processed" : "pending";
            DateTimeOffset? processedAt = alreadyApplied ? now : null;
            await inbox.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO snowshot.account_platform_events AS event
                    ("Id","EventName","MerchantOrderId","RoutingMetadataComplete","PayloadEncrypted","State","ReceivedAt","ProcessedAt","Attempts")
                VALUES ({id},{name},{merchantOrderId},TRUE,{encrypted},{state},clock_timestamp(),{processedAt},0)
                ON CONFLICT ("Id") DO UPDATE SET "MerchantOrderId"=EXCLUDED."MerchantOrderId","RoutingMetadataComplete"=TRUE,
                    "State"=CASE WHEN EXCLUDED."State"='processed' THEN 'processed' ELSE event."State" END,
                    "ProcessedAt"=COALESCE(event."ProcessedAt",EXCLUDED."ProcessedAt"),
                    "LeaseExpiresAt"=CASE WHEN EXCLUDED."State"='processed' THEN NULL ELSE event."LeaseExpiresAt" END
                """, token);
            return true;
        }, token);
        await using var db = await store.OpenAsync(token);
        if (name == "xpay_goods_deliver_notify")
        {
            // A delivery success response means the goods have actually been
            // granted. The retained inbox also recovers interrupted callbacks.
            var acknowledgeDelivery = await ProcessEventAsync(new() { Id = id, EventName = name, PayloadEncrypted = encrypted, State = "pending" }, token, true);
            await db.AccountPlatformEvents.Where(x => x.Id == id).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.State, "processed").SetProperty(x => x.ProcessedAt, clock.GetUtcNow())
                    .SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), token);
            if (!acknowledgeDelivery)
                throw new AccountException("order_refunded", "The payment has already been refunded.", 409);
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
                orders = await db.AccountPaymentOrders.Where(x => (x.State == "payment_pending" || x.State == "reconciliation_required" ||
                    ((x.State == "closed" || x.State == "failed") && !x.Fulfilled &&
                     (x.ClosedWithPositivePaidFee || x.PaidFen > 0 || x.PlatformTransactionId != null ||
                      db.AccountRefunds.Any(refund => refund.OrderId == x.Id && refund.State == "completed" && !refund.Applied)))) &&
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
                var order = await db.AccountPaymentOrders.AsNoTracking().SingleAsync(x => x.Id == refund.OrderId, token);
                var result = await platform.QueryAsync(refund.Id, order.OpenId, token);
                var verified = result?.Refunds.SingleOrDefault(x => x.RefundId == refund.Id);
                if (verified is null || result!.TransactionId != refund.PlatformRefundId)
                    throw new AccountException("reconciliation_required", "The retained refund has no matching platform result.", 409);
                VerifiedVirtualOrder? originalPayment = null;
                if (verified.State == "completed")
                {
                    originalPayment = await RefundPaymentEvidenceAsync(order, token);
                    if (originalPayment is null || !MatchesPaymentEvidence(order, originalPayment) ||
                        !await HasRefundSuccessEvidenceAsync(db, order, originalPayment.TransactionId!, refund.Id,
                            refund.PlatformRefundId, refund.RefundFen, token))
                        throw new AccountException("reconciliation_required", "The retained refund has no matching original payment evidence.", 409);
                }
                await RecordRefundAsync(order.Id, refund.PlatformRefundId, verified, originalPayment is not null, originalPayment, lease, token);
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

    private async Task<bool> ProcessEventAsync(AccountPlatformEventEntity entry, CancellationToken token, bool acknowledgeDelivery = false)
    {
        var fields = WechatPlatformMessage.Parse(secrets.Unprotect(entry.PayloadEncrypted));
        var orderId = Field(fields, entry.EventName == "xpay_refund_notify" ? "MchOrderId" : "OutTradeNo");
        if (entry.EventName is not ("xpay_goods_deliver_notify" or "xpay_refund_notify")) return true;
        if (string.IsNullOrWhiteSpace(orderId)) throw new AccountException("reconciliation_required", "The payment event has no merchant order.", 409);
        var refundNotification = entry.EventName == "xpay_refund_notify"
            ? ReadRefundNotification(fields) ?? throw new AccountException("reconciliation_required", "The refund notification is missing identifiers or a valid amount.", 409)
            : null;
        AccountPaymentOrderEntity order;
        await using (var db = await store.OpenAsync(token))
        {
            if (refundNotification is not null && await IsAppliedRefundAsync(db, refundNotification, token)) return true;
            order = await db.AccountPaymentOrders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId, token) ?? throw NotFound();
        }
        if (Field(fields, "OpenId") != order.OpenId) throw LinkConflict();
        if (entry.EventName == "xpay_goods_deliver_notify")
        {
            if (order.State == AccountPurchaseLifecycle.Refunded) return false;
            await using (var deleted = await store.OpenAsync(token))
                if (await deleted.Accounts.AnyAsync(x => x.Id == order.AccountId && x.DeletedAt != null, token))
                {
                    if (order.Fulfilled && order.State == "paid") return true;
                    throw new AccountException("reconciliation_required", "A payment for a deleted account requires manual reconciliation.", 409);
                }
            var parameters = JsonSerializer.Deserialize<VirtualPaymentParameters>(secrets.Unprotect(order.PaymentParametersEncrypted), Json)
                ?? throw new AccountException("reconciliation_required", "The original signed order is unavailable.", 409);
            var queried = string.IsNullOrWhiteSpace(fields["WeChatPayInfo.MchOrderNo"])
                ? await platform.QueryAsync(order.Id, order.OpenId, token) : null;
            var delivery = fields.Delivery(order.Id, order.OpenId, order.PriceFen, parameters, queried, acknowledgeDelivery);
            if (!await ApplyPaymentAsync(delivery, token))
            {
                await using var read = await store.OpenAsync(token);
                if (await read.AccountPaymentOrders.AnyAsync(x => x.Id == order.Id && x.State == AccountPurchaseLifecycle.Refunded, token))
                    return false;
                throw new AccountException("reconciliation_required", "The delivery requires payment reconciliation.", 409);
            }
            if (!acknowledgeDelivery) await NotifyDeliveryAsync(order.Id, token);
            return true;
        }
        if (refundNotification is { } notification)
        {
            var query = await platform.QueryAsync(notification.RefundId, order.OpenId, token);
            var refund = query?.Refunds.SingleOrDefault(x => x.RefundId == notification.RefundId);
            if (refund is null || refund.RefundFen <= 0) throw new AccountException("reconciliation_required", "The refund result is not yet available.", 409);
            var payment = await RefundPaymentEvidenceAsync(order, token);
            if (query!.TransactionId != notification.PlatformRefundId || payment is null || payment.TransactionId != notification.PlatformOrderId ||
                notification.AmountFen != refund.RefundFen)
                throw new AccountException("reconciliation_required", "The refund notification identifiers or amount could not be verified.", 409);
            await RecordRefundAsync(order.Id, notification.PlatformRefundId, refund, notification.SuccessNotified, payment, lease: null, token);
        }
        await RefreshPaymentAsync(order.Id, token);
        return true;
    }

    private async Task<VerifiedVirtualOrder?> RefundPaymentEvidenceAsync(AccountPaymentOrderEntity order, CancellationToken token)
    {
        if (order.PaidFen is > 0 and var paidFen && !string.IsNullOrWhiteSpace(order.PlatformTransactionId))
            return new(order.Id, order.OpenId, paidFen, "paid", order.PlatformTransactionId, []);
        return await platform.QueryAsync(order.Id, order.OpenId, token);
    }

    private async Task RecordRefundAsync(string orderId, string platformId, VerifiedVirtualRefund verified,
        bool successNotified, VerifiedVirtualOrder? originalPayment, AccountRetryLease? lease, CancellationToken token)
    {
        var (refundId, amountFen, platformState) = verified;
        await store.ExecuteAsync(async (db, now) =>
        {
            await AccountSynchronization.ReadIdentityAsync(db, token);
            var accountId = await db.AccountPaymentOrders.Where(x => x.Id == orderId).Select(x => x.AccountId).SingleAsync(token);
            await AccountSynchronization.AccountAsync(db, accountId, token);
            if (!string.IsNullOrWhiteSpace(originalPayment?.TransactionId))
                await AccountSynchronization.EvidenceAsync(db, "platform-payment", originalPayment.TransactionId, token);
            await AccountSynchronization.EvidenceAsync(db, "refund", refundId, token);
            await AccountSynchronization.EvidenceAsync(db, "platform-refund", platformId, token);
            var order = await db.AccountPaymentOrders.SingleAsync(x => x.Id == orderId, token);
            var previous = await db.AccountRefunds.FromSqlInterpolated($"SELECT * FROM snowshot.account_refunds WHERE \"Id\"={refundId} FOR UPDATE").SingleOrDefaultAsync(token);
            if (lease is not null && previous?.QueryLeaseToken != lease.Token) return false;
            if (originalPayment is not null)
            {
                if (!MatchesPaymentEvidence(order, originalPayment) || await db.AccountPaymentOrders.AnyAsync(
                    x => x.Id != order.Id && x.PlatformTransactionId == originalPayment.TransactionId, token))
                    throw new AccountException("reconciliation_required", "The refund conflicts with the retained original payment.", 409);
                order.PlatformTransactionId ??= originalPayment.TransactionId;
                order.PaidFen ??= originalPayment.PaidFen;
            }
            if (await db.AccountRefunds.AnyAsync(x => x.Id != refundId && x.PlatformRefundId == platformId, token))
                throw new AccountException("reconciliation_required", "The platform refund is already associated with another retained refund.", 409);
            if (previous is not null && (previous.OrderId != orderId || previous.RefundFen != amountFen || previous.PlatformRefundId != platformId))
                throw new AccountException("reconciliation_required", "The refund conflicts with retained financial evidence.", 409);
            if (previous?.State == "completed") return true;
            // Merge authenticated callback evidence under the same lock as the financial transition.
            var hasSuccessNotification = successNotified || (previous?.SuccessNotified == true &&
                (platformState != "completed" || await HasRefundSuccessEvidenceAsync(db, order, order.PlatformTransactionId!,
                    refundId, platformId, amountFen, token)));
            var state = platformState switch
            {
                "failed" => "failed",
                "completed" when hasSuccessNotification => "completed",
                _ => "processing"
            };
            var otherCompleted = await db.AccountRefunds.Where(x => x.OrderId == orderId && x.Id != refundId && x.State == "completed").SumAsync(x => (long)x.RefundFen, token);
            var paymentLimit = order.PaidFen ?? order.PriceFen;
            var exceedsRemainingBalance = otherCompleted + amountFen > paymentLimit;
            if (previous?.State == "failed" && state == "processing" && platformState != "completed" &&
                (!successNotified || exceedsRemainingBalance))
            {
                // A pending observation alone cannot reopen a failed attempt. An authenticated success notice
                // can recover a legacy failure when the remaining balance covers the refund.
                previous.SuccessNotified = hasSuccessNotification;
                return true;
            }
            // A failed attempt has no financial effect, so it does not consume the remaining refundable amount.
            if (amountFen <= 0 || amountFen > order.PriceFen ||
                (state != "failed" && exceedsRemainingBalance))
                throw new AccountException("reconciliation_required", "The refund exceeds the verified payment amount.", 409);
            var refund = previous ?? new AccountRefundEntity { Id = refundId, OrderId = orderId, RefundFen = amountFen, State = state, PlatformRefundId = platformId };
            if (previous is null) db.AccountRefunds.Add(refund);
            // Fence in-flight retries on terminal transitions and make a reopened refund immediately eligible.
            if (state != "processing" || refund.State == "failed")
            {
                refund.QueryLeaseToken = null;
                refund.QueryLeaseExpiresAt = null;
                refund.NextQueryAt = null;
            }
            else if (hasSuccessNotification && !refund.SuccessNotified)
            {
                refund.NextQueryAt = null;
            }
            refund.State = state;
            refund.UpdatedAt = now;
            refund.SuccessNotified = hasSuccessNotification;
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
            else if (state == "completed" && !order.Fulfilled && order.PaidFen is > 0 and var paidFen &&
                     !string.IsNullOrWhiteSpace(order.PlatformTransactionId) && otherCompleted + amountFen == paidFen)
            {
                var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == order.RequestId, token);
                if (AccountPurchaseLifecycle.IsExpiredRegistration(request) &&
                    await UnappliedRefundsMatchPaymentAsync(db, order, token))
                {
                    await CompleteRefundedRegistrationAsync(db, order, request, paidFen, now, token);
                    refund.Applied = true;
                }
            }
            return true;
        }, token);
    }

    private async Task<bool> UnappliedRefundsMatchPaymentAsync(SnowShotDbContext db, AccountPaymentOrderEntity order, CancellationToken token)
    {
        var refunds = await db.AccountRefunds.Where(x => x.OrderId == order.Id && x.State == "completed" && !x.Applied)
            .Select(x => new { x.Id, x.PlatformRefundId, x.RefundFen }).ToListAsync(token);
        foreach (var refund in refunds)
            if (!await HasRefundSuccessEvidenceAsync(db, order, order.PlatformTransactionId!,
                refund.Id, refund.PlatformRefundId, refund.RefundFen, token)) return false;
        return true;
    }

    private async Task<bool> HasRefundSuccessEvidenceAsync(SnowShotDbContext db, AccountPaymentOrderEntity order,
        string paymentId, string refundId, string platformRefundId, int amountFen, CancellationToken token)
    {
        // Old refund rows do not retain the original transaction ID. Recover it from the authenticated inbox.
        var events = db.AccountPlatformEvents.Where(x => x.EventName == "xpay_refund_notify");
        var payloads = events.Where(x => x.MerchantOrderId == order.Id).Select(x => x.PayloadEncrypted)
            .Concat(events.Where(x => x.MerchantOrderId == null && !x.RoutingMetadataComplete).Select(x => x.PayloadEncrypted))
            .AsAsyncEnumerable();
        await foreach (var payload in payloads.WithCancellation(token))
        {
            try
            {
                var notification = ReadRefundNotification(WechatPlatformMessage.Parse(secrets.Unprotect(payload)));
                if (notification is { SuccessNotified: true } && notification.OrderId == order.Id &&
                    notification.OpenId == order.OpenId && notification.PlatformOrderId == paymentId &&
                    notification.RefundId == refundId && notification.PlatformRefundId == platformRefundId &&
                    notification.AmountFen == amountFen) return true;
            }
            catch (Exception error) when (error is CryptographicException or FormatException or AccountException)
            {
                // Unreadable evidence cannot authorize a financial change.
            }
        }
        return false;
    }

    private static RefundNotification? ReadRefundNotification(WechatPlatformMessage fields)
    {
        var orderId = Field(fields, "MchOrderId");
        var openId = Field(fields, "OpenId");
        var refundId = Field(fields, "MchRefundId");
        var platformRefundId = Field(fields, "WxRefundId");
        var platformOrderId = Field(fields, "WxOrderId");
        var retCode = Field(fields, "RetCode");
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(openId) || string.IsNullOrWhiteSpace(refundId) ||
            string.IsNullOrWhiteSpace(platformRefundId) || string.IsNullOrWhiteSpace(platformOrderId) || retCode is null ||
            !int.TryParse(Field(fields, "RefundFee"), NumberStyles.None, CultureInfo.InvariantCulture, out var amountFen) || amountFen <= 0)
            return null;
        return new(orderId, openId, refundId, platformRefundId, platformOrderId, amountFen, retCode == "0");
    }

    private static Task<bool> IsAppliedRefundAsync(SnowShotDbContext db, RefundNotification notification, CancellationToken token) =>
        MatchingRefunds(db, notification).AnyAsync(refund => refund.State == "completed" && refund.Applied &&
            db.AccountPaymentOrders.Any(order => order.Id == refund.OrderId && order.OpenId == notification.OpenId &&
                order.PlatformTransactionId == notification.PlatformOrderId), token);

    private static async Task<bool> TryRequeueUnsettledRefundNotificationAsync(SnowShotDbContext db, RefundNotification notification,
        string eventId, string encrypted, CancellationToken token)
    {
        var processed = db.AccountPlatformEvents.Where(x => x.Id == eventId && x.EventName == "xpay_refund_notify" && x.State == "processed");
        if (!await processed.AnyAsync(token)) return false;
        var matchingOrder = db.AccountPaymentOrders.Where(x => x.Id == notification.OrderId && x.OpenId == notification.OpenId &&
            (x.PlatformTransactionId == null || x.PlatformTransactionId == notification.PlatformOrderId));
        var accountId = await matchingOrder.Select(x => (Guid?)x.AccountId).SingleOrDefaultAsync(token);
        if (accountId is null) return false;

        // Recheck the refund and its remaining amount under the same lock used to apply it.
        await AccountSynchronization.AccountAsync(db, accountId.Value, token);
        var matchingRefund = MatchingRefunds(db, notification);
        var recoverable = await matchingOrder.AnyAsync(order => order.State != AccountPurchaseLifecycle.Refunded &&
            matchingRefund.Any(refund => !refund.Applied && (refund.State == "failed" ||
                (refund.State == "completed" && !order.Fulfilled && (order.PaidFen == null || order.PlatformTransactionId == null)))) &&
            db.AccountRefunds.Where(refund => refund.OrderId == order.Id && refund.Id != notification.RefundId && refund.State == "completed")
                .Sum(refund => (long)refund.RefundFen) + notification.AmountFen <= (order.PaidFen ?? order.PriceFen), token);
        if (!recoverable) return false;

        // Reuse the event identity and refresh its ciphertext from the authenticated replay.
        return await processed.ExecuteUpdateAsync(set => set.SetProperty(x => x.State, "pending")
            .SetProperty(x => x.MerchantOrderId, notification.OrderId).SetProperty(x => x.RoutingMetadataComplete, true)
            .SetProperty(x => x.PayloadEncrypted, encrypted)
            .SetProperty(x => x.ProcessedAt, (DateTimeOffset?)null).SetProperty(x => x.LeaseExpiresAt, (DateTimeOffset?)null), token) != 0;
    }

    private static IQueryable<AccountRefundEntity> MatchingRefunds(SnowShotDbContext db, RefundNotification notification) =>
        db.AccountRefunds.Where(refund => refund.Id == notification.RefundId && refund.OrderId == notification.OrderId &&
            refund.PlatformRefundId == notification.PlatformRefundId && refund.RefundFen == notification.AmountFen);

    private sealed record RefundNotification(string OrderId, string OpenId, string RefundId, string PlatformRefundId,
        string PlatformOrderId, int AmountFen, bool SuccessNotified);

    private static string? Field(WechatPlatformMessage fields, string key) => fields[key];
}
