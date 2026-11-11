using Microsoft.EntityFrameworkCore;
using SnowShot.Infrastructure.Persistence;

namespace SnowShot.Infrastructure.Accounts;

/// <summary>
/// Owns coupled request/order transitions. Callers hold the identity/account locks
/// and persist these mutations together with their financial effects.
/// Existing state codes remain valid for retained orders and requests.
/// </summary>
internal static class AccountPurchaseLifecycle
{
    internal const string Refunded = "refunded";

    internal static void IssueCheckout(AccountPurchaseRequestEntity request, string orderId)
    {
        RequireUnissued(request);
        request.OrderId = orderId;
        request.State = "payment_pending";
    }

    internal static void CancelUnissued(AccountPurchaseRequestEntity request)
    {
        RequireUnissued(request);
        request.State = "failed";
        request.CodeImage = null;
    }

    internal static bool CanRecoverCheckout(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request) =>
        order.RequestId == request.Id && request.OrderId == order.Id &&
        order.State == "payment_pending" && request.State == "payment_pending" &&
        !order.Fulfilled && order.VerifiedAt is null && order.PlatformTransactionId is null;

    internal static bool IsExpiredRegistration(AccountPurchaseRequestEntity request) =>
        request.Purpose == "registration" && request.State == "expired" && request.OrderId is not null;

    internal static bool HasRetainedRegistrationOrder(AccountPurchaseRequestEntity request) =>
        request.Purpose == "registration" && request.State is ("expired" or "failed") && request.OrderId is not null;

    internal static bool HasRetainedPaymentEvidence(AccountPaymentOrderEntity order) =>
        order.ClosedWithPositivePaidFee || order.PaidFen is > 0 || order.PlatformTransactionId is not null;

    internal static async Task<bool> HasEvidenceAgainstUnpaidClosureAsync(SnowShotDbContext db, AccountPaymentOrderEntity order,
        CancellationToken token) => HasRetainedPaymentEvidence(order) || await db.AccountRefunds.AnyAsync(
            x => x.OrderId == order.Id && x.State == "completed" && !x.Applied, token);

    internal static async Task<bool> RequiresClosureReconciliationAsync(SnowShotDbContext db, AccountPaymentOrderEntity order,
        CancellationToken token) => !order.Fulfilled && order.State is ("closed" or "failed") &&
            await HasEvidenceAgainstUnpaidClosureAsync(db, order, token);

    internal static async Task<string> ReportedOrderStateAsync(SnowShotDbContext db, AccountPaymentOrderEntity order,
        CancellationToken token) => await RequiresClosureReconciliationAsync(db, order, token) ? "reconciliation_required" : order.State;

    internal static void RequireReconciliation(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request)
    {
        RequireMatchingOrder(order, request);
        order.State = "reconciliation_required";
        if (!IsExpiredRegistration(request)) request.State = "reconciliation_required";
    }

    internal static void CloseUnfulfilled(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request)
    {
        RequireMatchingOrder(order, request);
        if (order.Fulfilled)
            throw new InvalidOperationException("A fulfilled payment cannot release its identity claims by closing.");
        order.State = "closed";
        if (!IsExpiredRegistration(request)) request.State = "failed";
    }

    internal static void CompleteRefundedRegistration(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request)
    {
        RequireMatchingOrder(order, request);
        if (order.Fulfilled || !IsExpiredRegistration(request))
            throw new InvalidOperationException("Only an unfulfilled expired registration can close without applying wallet credits.");
        order.State = Refunded;
    }

    internal static void CompletePayment(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request, bool delivered)
    {
        RequireMatchingOrder(order, request);
        if (!order.Fulfilled)
            throw new InvalidOperationException("Payment completion requires fulfillment effects in the same transaction.");
        order.DeliveryConfirmed |= delivered;
        order.State = "paid";
        if (!IsExpiredRegistration(request) || request.FulfilledAt is not null) request.State = "fulfilled";
    }

    internal static void CompleteAccountLink(AccountPurchaseRequestEntity request, DateTimeOffset now)
    {
        RequireUnissued(request);
        request.State = "fulfilled";
        request.CompletionKind = "account_link";
        request.FulfilledAt = now;
    }

    private static void RequireUnissued(AccountPurchaseRequestEntity request)
    {
        if (request.State != "awaiting_payment" || request.OrderId is not null)
            throw new InvalidOperationException("Only an unissued purchase request can begin checkout or adopt credentials.");
    }

    private static void RequireMatchingOrder(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request)
    {
        if (order.RequestId != request.Id || request.OrderId != order.Id || order.AccountId != request.AccountId)
            throw new InvalidOperationException("A payment transition must apply to its original request and account.");
    }
}
