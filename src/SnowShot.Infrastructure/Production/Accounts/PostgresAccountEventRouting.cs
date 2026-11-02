using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SnowShot.Application;

namespace SnowShot.Infrastructure.Accounts;

internal sealed partial class PostgresAccountEventInbox(PostgresAccountStore store, AccountSecrets secrets,
    ILogger<PostgresAccountEventInbox> logger)
{
    private const int RefundRoutingBatchSize = 100;

    internal async Task BackfillRefundRoutingAsync(CancellationToken token)
    {
        // Routing is derived solely from retained authenticated evidence. This
        // transaction owns only a bounded set of inbox rows, never identity or
        // wallet locks; other replicas skip rows already being indexed.
        await store.ExecuteAsync(async (db, attemptedAt) =>
        {
            var events = await db.AccountPlatformEvents.FromSqlInterpolated($"""
                SELECT * FROM snowshot.account_platform_events
                WHERE "EventName"='xpay_refund_notify' AND "State"<>'processed' AND NOT "RoutingMetadataComplete"
                ORDER BY "RoutingLastAttemptAt" NULLS FIRST,"Id" FOR UPDATE SKIP LOCKED LIMIT {RefundRoutingBatchSize}
                """).ToListAsync(token);
            foreach (var entry in events)
            {
                // Rotate unreadable rows behind never-attempted evidence so a
                // large damaged batch cannot starve later recoverable records.
                entry.RoutingLastAttemptAt = attemptedAt;
                try
                {
                    var fields = WechatPlatformMessage.Parse(secrets.Unprotect(entry.PayloadEncrypted));
                    entry.MerchantOrderId = MerchantOrderId(fields, entry.EventName);
                    entry.RoutingMetadataComplete = true;
                }
                catch (Exception error) when (error is CryptographicException or FormatException or AccountException)
                {
                    // Preserve unknown evidence and the deletion guard while
                    // allowing other records and payment reconciliation to run.
                    RefundRoutingUnavailable(logger, entry.Id,
                        error is AccountException account ? account.Code : error.GetType().Name);
                }
            }
            return events.Count;
        }, token);
    }

    internal static string? MerchantOrderId(WechatPlatformMessage fields, string eventName)
    {
        var value = fields[eventName == "xpay_refund_notify" ? "MchOrderId" : "OutTradeNo"];
        // Merchant IDs issued by this service have at most 32 characters. A
        // malformed larger value cannot refer to a retained payment order.
        return value is { Length: > 0 and <= 32 } ? value : null;
    }

    [LoggerMessage(Level = LogLevel.Error, EventId = 5505,
        Message = "Retained refund event {EventId} has unreadable routing evidence: {Reason}; account deletion remains blocked")]
    private static partial void RefundRoutingUnavailable(ILogger logger, string eventId, string reason);
}
