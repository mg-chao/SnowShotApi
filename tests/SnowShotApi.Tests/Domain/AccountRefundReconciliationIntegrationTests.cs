using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task RefundCallbackAndLegacyRetryCannotDebitADifferentPaymentTransaction()
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        var retainedTransaction = "wx-" + order.OrderId;
        await f.Service.VerifyCallbackAsync("", "", "", null,
            DeliveryPayload(order.OrderId, payer.OpenId!, retainedTransaction), "", Token);
        var active = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);

        var conflictingTransaction = "different-" + order.OrderId;
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", conflictingTransaction, [], 990);
        var refundId = Guid.NewGuid().ToString("N");
        var platformRefundId = "wx-" + refundId;
        f.Platform.Orders[refundId] = new(refundId, payer.OpenId!, 0, "refund", platformRefundId,
            [new(refundId, 490, "completed")]);
        var payload = JsonSerializer.Serialize(new
        {
            Event = "xpay_refund_notify",
            MchOrderId = order.OrderId,
            WxOrderId = conflictingTransaction,
            MchRefundId = refundId,
            WxRefundId = platformRefundId,
            RefundFee = 490,
            RetCode = 0,
            payer.OpenId
        });
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        await f.Service.ReconcileAsync(Token);

        await using var db = new SnowShotDbContext(f.Options);
        var balance = db.AccountWallets.Where(x => x.AccountId == active.AccountId).Select(x => x.AvailableNanoYuan);
        Assert.Equal(9_900_000_000L, await balance.SingleAsync(Token));
        Assert.False(await db.AccountRefunds.AnyAsync(x => x.Id == refundId, Token));

        // Simulate a pending refund accepted before the original transaction was checked.
        db.AccountRefunds.Add(new()
        {
            Id = refundId,
            OrderId = order.OrderId,
            PlatformRefundId = platformRefundId,
            RefundFen = 490,
            State = "processing",
            SuccessNotified = true,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Token);
        f.Platform.Orders[order.OrderId] = f.Platform.Orders[order.OrderId] with { TransactionId = retainedTransaction };
        await f.Service.ReconcileAsync(Token);
        Assert.Equal(9_900_000_000L, await balance.SingleAsync(Token));

        await SendRefundNotificationAsync(f, payer, order, refundId, 490, "completed", 0);
        Assert.Equal(5_000_000_000L, await balance.SingleAsync(Token));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task ClosedResultCannotDiscardPaidUnfulfilledRegistration(bool historicallyClosed)
    {
        var (f, payer, registration, order) = await ExpiredRegistrationWithReplacementAsync(samePayer: true);
        Assert.Equal("reconciliation_required", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        await using var db = new SnowShotDbContext(f.Options);
        var payments = db.AccountPaymentOrders.Where(x => x.Id == order.OrderId);
        var ledger = db.AccountFinancialEntries.Where(x => x.Id == "topup:" + order.OrderId);
        if (historicallyClosed)
        {
            await payments.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "closed"), Token);
            await ledger.ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "failed"), Token);
            var privateStatus = await f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, false, Token);
            Assert.Equal("expired", privateStatus.State);
            Assert.Equal("reconciliation_required", privateStatus.OrderState);
            var payerStatus = await f.Service.PublicRequestAsync(registration.RequestId, payer, Token);
            Assert.Equal("reconciliation_required", payerStatus.OrderState);
            var anonymousStatus = await f.Service.PublicRequestAsync(registration.RequestId, null, Token);
            Assert.Null(anonymousStatus.OrderState);
            Assert.Null(anonymousStatus.OrderId);
            Assert.Equal("closed", await payments.Select(x => x.State).SingleAsync(Token));

            // Older writers could also mark the corresponding request failed.
            var requests = db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId);
            await requests.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "failed"), Token);
            var historicalStatus = await f.Service.RegistrationAsync(registration.RequestId,
                registration.RegistrationProof!, false, Token);
            Assert.Equal("expired", historicalStatus.State);
            Assert.Equal("reconciliation_required", historicalStatus.OrderState);
            Assert.Equal("expired", await requests.Select(x => x.State).SingleAsync(Token));
        }
        Assert.Contains("pending_payment", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);

        await ReleaseQueryCooldown(f, order.OrderId);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 0, "closed", null, [], 990);
        var status = await f.Service.RefreshOrderAsync(payer, order.OrderId, Token);
        Assert.Equal("reconciliation_required", status.State);
        Assert.Equal(990, await payments.Select(x => x.PaidFen).SingleAsync(Token));
        Assert.Equal("pending", await ledger.Select(x => x.Status).SingleAsync(Token));
        Assert.Contains("pending_payment", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);

        await SendRefundNotificationAsync(f, payer, order, Guid.NewGuid().ToString("N"), 990, "completed", 0);
        Assert.Equal("refunded", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LegacyClosedRegistrationRefundIsRecoveredFromVerifiedPayment()
    {
        await CheckLegacyClosedRegistrationRefundAsync(replayNotification: false);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LegacyClosedRegistrationRefundCanReplayItsProcessedNotification()
    {
        await CheckLegacyClosedRegistrationRefundAsync(replayNotification: true);
    }

    private static async Task CheckLegacyClosedRegistrationRefundAsync(bool replayNotification)
    {
        var (f, payer, registration, order) = await ExpiredRegistrationWithReplacementAsync(samePayer: true);
        var refundId = Guid.NewGuid().ToString("N");
        var notice = await SendRefundNotificationAsync(f, payer, order, refundId, 990, "completed", 0);
        var verifiedPayment = f.Platform.Orders[order.OrderId];

        await using var db = new SnowShotDbContext(f.Options);
        var payments = db.AccountPaymentOrders.Where(x => x.Id == order.OrderId);
        var refunds = db.AccountRefunds.Where(x => x.Id == refundId);
        // The old writer completed the refund without storing the original payment evidence.
        await payments.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "closed")
            .SetProperty(x => x.PaidFen, (int?)null).SetProperty(x => x.PlatformTransactionId, (string?)null), Token);
        await refunds.ExecuteUpdateAsync(s => s.SetProperty(x => x.Applied, false), Token);
        Assert.Equal("reconciliation_required", (await f.Service.RegistrationAsync(registration.RequestId,
            registration.RegistrationProof!, false, Token)).OrderState);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);

        if (replayNotification)
        {
            await f.Service.VerifyCallbackAsync("", "", "", null, notice, "", Token);
            var eventId = Convert.ToHexStringLower(AccountSecrets.Hash(notice));
            Assert.Equal("pending", await db.AccountPlatformEvents.Where(x => x.Id == eventId).Select(x => x.State).SingleAsync(Token));
        }
        else
        {
            f.Platform.Orders[order.OrderId] = verifiedPayment with { State = "closed", PaidFen = 0, TransactionId = null };
            await ReleaseQueryCooldown(f, order.OrderId);
            await f.Service.ReconcileAsync(Token);
            Assert.Equal("reconciliation_required", await payments.Select(x => x.State).SingleAsync(Token));
            Assert.False(await refunds.Select(x => x.Applied).SingleAsync(Token));
            f.Platform.Orders[order.OrderId] = verifiedPayment;
            await ReleaseQueryCooldown(f, order.OrderId);
        }

        await f.Service.ReconcileAsync(Token);
        var payment = await payments.SingleAsync(Token);
        Assert.Equal("refunded", payment.State);
        Assert.False(payment.Fulfilled);
        Assert.True(await refunds.Select(x => x.Applied).SingleAsync(Token));
        Assert.Equal(0, await db.AccountWallets.Where(x => x.AccountId == payment.AccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ClosedResultWithPositivePaidFeeKeepsOrderInReconciliationUntilVerifiedPayment()
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 490, "closed", "conflicting-" + order.OrderId, [], 990);
        Assert.Equal("reconciliation_required", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);

        await ReleaseQueryCooldown(f, order.OrderId);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 0, "closed", null, [], 990);
        Assert.Equal("reconciliation_required", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        Assert.Contains("pending_payment", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);
        await using var db = new SnowShotDbContext(f.Options);
        var retained = await db.AccountPaymentOrders.AsNoTracking().SingleAsync(x => x.Id == order.OrderId, Token);
        Assert.True(retained.ClosedWithPositivePaidFee);
        Assert.Null(retained.PaidFen);
        Assert.Null(retained.PlatformTransactionId);
        Assert.Equal("pending", await db.AccountFinancialEntries.Where(x => x.Id == "topup:" + order.OrderId)
            .Select(x => x.Status).SingleAsync(Token));

        await ReleaseQueryCooldown(f, order.OrderId);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990, Delivered: true);
        Assert.Equal("paid", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        Assert.Equal("fulfilled", (await f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, false, Token)).State);
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task SuccessNotificationSurvivesPendingQueriesAndCompletesRefundOnce(bool previouslyMarkedFailed)
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        var transactionId = "wx-" + order.OrderId;
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, payer.OpenId!, transactionId), "", Token);
        var active = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", transactionId, [], 990);
        var refundId = Guid.NewGuid().ToString("N");
        await SendRefundNotificationAsync(f, payer, order, refundId, 490, "processing", 1);

        await using var db = new SnowShotDbContext(f.Options);
        var refunds = db.AccountRefunds.Where(x => x.Id == refundId);
        var pending = await refunds.AsNoTracking().SingleAsync(Token);
        Assert.Equal("processing", pending.State);
        Assert.False(pending.SuccessNotified);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);

        var oldLease = Guid.NewGuid();
        var oldDeadline = DateTimeOffset.UtcNow.AddHours(1);
        if (previouslyMarkedFailed)
            await refunds.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "failed")
                .SetProperty(x => x.QueryLeaseToken, oldLease)
                .SetProperty(x => x.QueryLeaseExpiresAt, oldDeadline)
                .SetProperty(x => x.NextQueryAt, oldDeadline), Token);

        var notice = await SendRefundNotificationAsync(f, payer, order, refundId, 490, "processing", 0);
        var awaitingConfirmation = await refunds.AsNoTracking().SingleAsync(Token);
        Assert.Equal("processing", awaitingConfirmation.State);
        Assert.True(awaitingConfirmation.SuccessNotified);
        Assert.False(awaitingConfirmation.Applied);
        Assert.NotEqual(oldLease, awaitingConfirmation.QueryLeaseToken);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);
        Assert.Equal(9_900_000_000L, await db.AccountWallets.Where(x => x.AccountId == active.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));

        f.Platform.Orders[refundId] = f.Platform.Orders[refundId] with { Refunds = [new(refundId, 490, "completed")] };
        await refunds.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextQueryAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        await f.Service.ReconcileAsync(Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, " " + notice, "", Token);
        await f.Service.ReconcileAsync(Token);
        var completed = await refunds.AsNoTracking().SingleAsync(Token);
        Assert.Equal("completed", completed.State);
        Assert.True(completed.Applied);
        Assert.Equal(5_000_000_000L, await db.AccountWallets.Where(x => x.AccountId == active.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task ExactSuccessReplayRecoversFailedRefundThroughNormalVerification(bool platformStillProcessing)
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        var transactionId = "wx-" + order.OrderId;
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, payer.OpenId!, transactionId), "", Token);
        var active = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", transactionId, [], 990);
        var refundId = Guid.NewGuid().ToString("N");
        var notice = await SendRefundNotificationAsync(f, payer, order, refundId, 490, "processing", 0);

        await using var db = new SnowShotDbContext(f.Options);
        var eventId = Convert.ToHexStringLower(AccountSecrets.Hash(notice));
        var events = db.AccountPlatformEvents.Where(x => x.Id == eventId);
        var originalEvent = await events.AsNoTracking().SingleAsync(Token);
        Assert.Equal("processed", originalEvent.State);
        var refunds = db.AccountRefunds.Where(x => x.Id == refundId);
        await refunds.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "failed")
            .SetProperty(x => x.SuccessNotified, platformStillProcessing), Token);
        await db.AccountFinancialEntries.Where(x => x.Id == "refund:" + refundId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.Status, "failed"), Token);
        if (!platformStillProcessing)
            f.Platform.Orders[refundId] = f.Platform.Orders[refundId] with { Refunds = [new(refundId, 490, "completed")] };

        await f.Service.VerifyCallbackAsync("", "", "", null, notice, "", Token);
        var queued = await events.AsNoTracking().SingleAsync(Token);
        Assert.Equal("pending", queued.State);
        Assert.Null(queued.ProcessedAt);
        Assert.Equal(originalEvent.Attempts, queued.Attempts);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);

        var lease = DateTimeOffset.UtcNow.AddMinutes(1);
        await events.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "processing")
            .SetProperty(x => x.LeaseExpiresAt, lease), Token);
        var retainedLease = await events.Select(x => x.LeaseExpiresAt).SingleAsync(Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, notice, "", Token);
        var inFlight = await events.AsNoTracking().SingleAsync(Token);
        Assert.Equal("processing", inFlight.State);
        Assert.Equal(retainedLease, inFlight.LeaseExpiresAt);
        await events.ExecuteUpdateAsync(s => s.SetProperty(x => x.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        await f.Service.ReconcileAsync(Token);

        if (platformStillProcessing)
        {
            var pending = await refunds.AsNoTracking().SingleAsync(Token);
            Assert.Equal("processing", pending.State);
            Assert.True(pending.SuccessNotified);
            Assert.False(pending.Applied);
            f.Platform.Orders[refundId] = f.Platform.Orders[refundId] with { Refunds = [new(refundId, 490, "completed")] };
            await refunds.ExecuteUpdateAsync(s => s.SetProperty(x => x.NextQueryAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
            await f.Service.ReconcileAsync(Token);
        }

        await f.Service.VerifyCallbackAsync("", "", "", null, notice, "", Token);
        await f.Service.ReconcileAsync(Token);
        Assert.Equal("processed", await events.Select(x => x.State).SingleAsync(Token));
        var completed = await refunds.AsNoTracking().SingleAsync(Token);
        Assert.Equal("completed", completed.State);
        Assert.True(completed.Applied);
        Assert.Equal(5_000_000_000L, await db.AccountWallets.Where(x => x.AccountId == active.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task SuccessReplaysDoNotReopenFailedRefundExceedingRemainingBalance()
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        var transactionId = "wx-" + order.OrderId;
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, payer.OpenId!, transactionId), "", Token);
        var active = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", transactionId, [], 990);
        var refundId = Guid.NewGuid().ToString("N");
        var notice = await SendRefundNotificationAsync(f, payer, order, refundId, 490, "failed", 0);
        await SendRefundNotificationAsync(f, payer, order, Guid.NewGuid().ToString("N"), 600, "completed", 0);

        f.Platform.FailQueries = true;
        await f.Service.VerifyCallbackAsync("", "", "", null, notice, "", Token);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);

        f.Platform.FailQueries = false;
        f.Platform.Orders[refundId] = f.Platform.Orders[refundId] with { Refunds = [new(refundId, 490, "processing")] };
        var equivalentNotice = " " + notice;
        await f.Service.VerifyCallbackAsync("", "", "", null, equivalentNotice, "", Token);
        await f.Service.ReconcileAsync(Token);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);

        await using var db = new SnowShotDbContext(f.Options);
        var eventId = Convert.ToHexStringLower(AccountSecrets.Hash(notice));
        Assert.Equal("processed", await db.AccountPlatformEvents.Where(x => x.Id == eventId).Select(x => x.State).SingleAsync(Token));
        var equivalentEventId = Convert.ToHexStringLower(AccountSecrets.Hash(equivalentNotice));
        Assert.Equal("processed", await db.AccountPlatformEvents.Where(x => x.Id == equivalentEventId).Select(x => x.State).SingleAsync(Token));
        var refund = await db.AccountRefunds.SingleAsync(x => x.Id == refundId, Token);
        Assert.Equal("failed", refund.State);
        Assert.False(refund.Applied);
        Assert.Equal(3_900_000_000L, await db.AccountWallets.Where(x => x.AccountId == active.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task VerifiedFailureOfEarlierRefundDoesNotBlockDeletionAfterFullRefund()
    {
        var (f, payer, _, order) = await ExpiredRegistrationWithReplacementAsync(samePayer: true);
        var earlierRefund = Guid.NewGuid().ToString("N");
        var processingNotice = await SendRefundNotificationAsync(f, payer, order, earlierRefund, 490, "processing", 0);
        await SendRefundNotificationAsync(f, payer, order, Guid.NewGuid().ToString("N"), 990, "completed", 0);
        Assert.Equal("refunded", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);

        await SendRefundNotificationAsync(f, payer, order, earlierRefund, 490, "failed", 1);
        f.Platform.FailQueries = true;
        await f.Service.VerifyCallbackAsync("", "", "", null, processingNotice, "", Token);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
        f.Platform.FailQueries = false;
        f.Platform.Orders[earlierRefund] = f.Platform.Orders[earlierRefund] with { Refunds = [new(earlierRefund, 490, "processing")] };
        await f.Service.VerifyCallbackAsync("", "", "", null, " " + processingNotice, "", Token);
        await f.Service.ReconcileAsync(Token);
        var status = await f.Service.RefreshOrderAsync(payer, order.OrderId, Token);
        Assert.Equal("refunded", status.State);
        Assert.Equal("completed", status.RefundStatus);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
        await using var read = new SnowShotDbContext(f.Options);
        var refund = await read.AccountRefunds.SingleAsync(x => x.Id == earlierRefund, Token);
        Assert.Equal("failed", refund.State);
        Assert.False(refund.Applied);
        Assert.Equal("failed", await read.AccountFinancialEntries.Where(x => x.Id == "refund:" + earlierRefund)
            .Select(x => x.Status).SingleAsync(Token));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task MatchingAppliedRefundNotificationsDoNotBlockDeletionDuringPlatformOutage(bool delayedFailure)
    {
        var (f, payer, _, order) = await ExpiredRegistrationWithReplacementAsync(samePayer: true);
        var refundId = Guid.NewGuid().ToString("N");
        var payload = await SendRefundNotificationAsync(f, payer, order, refundId, 990, "completed", 0);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);

        f.Platform.FailQueries = true;
        var duplicate = delayedFailure
            ? payload.Replace("\"RetCode\":0", "\"RetCode\":1", StringComparison.Ordinal)
            : " " + payload;
        Assert.NotEqual(payload, duplicate);
        await f.Service.VerifyCallbackAsync("", "", "", null, duplicate, "", Token);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
        await using var db = new SnowShotDbContext(f.Options);
        var duplicateId = Convert.ToHexStringLower(AccountSecrets.Hash(duplicate));
        var duplicateEvent = db.AccountPlatformEvents.Where(x => x.Id == duplicateId);
        Assert.Equal("processed", await duplicateEvent.Select(x => x.State).SingleAsync(Token));

        // Simulate a duplicate already queued before the applied refund was recognized at intake.
        await duplicateEvent.ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "pending")
            .SetProperty(x => x.ProcessedAt, (DateTimeOffset?)null), Token);
        await f.Service.ReconcileAsync(Token);
        Assert.Equal("processed", await duplicateEvent.Select(x => x.State).SingleAsync(Token));
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);

        var conflicting = payload.Replace("\"RefundFee\":990", "\"RefundFee\":989", StringComparison.Ordinal);
        Assert.NotEqual(payload, conflicting);
        await f.Service.VerifyCallbackAsync("", "", "", null, conflicting, "", Token);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);
        Assert.Equal(1, await db.AccountRefunds.CountAsync(x => x.OrderId == order.OrderId, Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DeletionClearsRefundedRegistrationCredentialsWithoutTouchingAnotherOwnersUsername()
    {
        var (f, payer, registration, order) = await ExpiredRegistrationWithReplacementAsync(samePayer: false);
        await SendRefundNotificationAsync(f, payer, order, Guid.NewGuid().ToString("N"), 990, "completed", 0);
        var state = await f.Service.DeletionEligibilityAsync(payer, Token);
        Assert.True(state.CanDelete);
        Assert.True((await f.Service.DeleteAsync(payer, new("DELETE", payer.OpenId!, true, state.DeletionVersion), Token)).Deleted);

        await using var db = new SnowShotDbContext(f.Options);
        var original = await db.AccountPaymentOrders.SingleAsync(x => x.Id == order.OrderId, Token);
        var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == registration.RequestId, Token);
        var account = await db.Accounts.SingleAsync(x => x.Id == original.AccountId, Token);
        Assert.NotNull(account.DeletedAt);
        Assert.Empty(account.PasswordHash);
        Assert.Null(request.ProofHash);
        Assert.Empty(original.PaymentParametersEncrypted);
        Assert.Equal("refunded", original.State);
        Assert.Equal(payer.OpenId, original.OpenId);
        Assert.Equal(990, original.PaidFen);
        Assert.True(await db.AccountRefunds.Where(x => x.OrderId == original.Id).Select(x => x.Applied).SingleAsync(Token));

        var replacement = await f.Service.LoginAsync(registration.Username!, "password-123", Name(), Token);
        Assert.NotEqual(account.Id, replacement.AccountId);
        Assert.Equal(9_900_000_000L, await db.AccountWallets.Where(x => x.AccountId == replacement.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    private static async Task<(TestFixture Fixture, AccountSessionIdentity Payer, AccountPurchaseRequest Registration, AccountPurchaseOrder Order)>
        ExpiredRegistrationWithReplacementAsync(bool samePayer)
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        var replacement = await Register(f, registration.Username!);
        var replacementPayer = samePayer ? payer : await Mini(f);
        var newOrder = await f.Service.CreateOrderAsync(replacementPayer, replacement.RequestId, "cny-9-9", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(newOrder.OrderId, replacementPayer.OpenId!,
            "wx-" + newOrder.OrderId), "", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        return (f, payer, registration, order);
    }

    private static async Task<string> SendRefundNotificationAsync(TestFixture f, AccountSessionIdentity payer, AccountPurchaseOrder order,
        string refundId, int amountFen, string state, int retCode)
    {
        f.Platform.Orders[refundId] = new(refundId, payer.OpenId!, 0, "refund", "wx-" + refundId, [new(refundId, amountFen, state)]);
        var payload = JsonSerializer.Serialize(new
        {
            Event = "xpay_refund_notify",
            MchOrderId = order.OrderId,
            WxOrderId = "wx-" + order.OrderId,
            MchRefundId = refundId,
            WxRefundId = "wx-" + refundId,
            RefundFee = amountFen,
            RetCode = retCode,
            payer.OpenId
        });
        await ReleaseQueryCooldown(f, order.OrderId);
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        await f.Service.ReconcileAsync(Token);
        return payload;
    }
}
