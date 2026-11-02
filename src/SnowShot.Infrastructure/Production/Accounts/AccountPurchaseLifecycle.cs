using SnowShot.Infrastructure.Persistence;

namespace SnowShot.Infrastructure.Accounts;

/// <summary>
/// Owns coupled request/order transitions. Callers hold the identity/account locks
/// and persist these mutations together with their financial effects.
/// State codes remain unchanged for existing database rows and API clients.
/// </summary>
internal static class AccountPurchaseLifecycle
{
    internal static void IssueCheckout(AccountPurchaseRequestEntity request, string orderId)
    {
        RequireUnissued(request);
        request.OrderId = orderId;
        request.State = "payment_pending";
    }

    internal static bool CanRecoverCheckout(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request) =>
        order.RequestId == request.Id && request.OrderId == order.Id &&
        order.State == "payment_pending" && request.State == "payment_pending" &&
        !order.Fulfilled && order.VerifiedAt is null && order.PlatformTransactionId is null;

    internal static void RequireReconciliation(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request)
    {
        RequireMatchingOrder(order, request);
        order.State = request.State = "reconciliation_required";
    }

    internal static void CloseUnfulfilled(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request)
    {
        RequireMatchingOrder(order, request);
        if (order.Fulfilled)
            throw new InvalidOperationException("A fulfilled payment cannot release its identity claims by closing.");
        order.State = "closed";
        request.State = "failed";
    }

    internal static void CompletePayment(AccountPaymentOrderEntity order, AccountPurchaseRequestEntity request, bool delivered)
    {
        RequireMatchingOrder(order, request);
        if (!order.Fulfilled)
            throw new InvalidOperationException("Payment completion requires fulfillment effects in the same transaction.");
        order.DeliveryConfirmed |= delivered;
        order.State = "paid";
        request.State = "fulfilled";
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
