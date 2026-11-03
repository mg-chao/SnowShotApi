using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed class AccountPurchaseLifecycleTests
{
    [Fact]
    public void CanceledUnissuedCodeCannotBeginCheckout()
    {
        var request = new AccountPurchaseRequestEntity
        {
            Purpose = "top_up",
            State = "awaiting_payment",
            CodeImage = [1, 2, 3]
        };
        AccountPurchaseLifecycle.CancelUnissued(request);
        Assert.Equal("failed", request.State);
        Assert.Null(request.CodeImage);
        Assert.Null(request.OrderId);
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.IssueCheckout(request, "order"));
    }

    [Fact]
    public void CancellationCannotDiscardIssuedPayment()
    {
        var (order, request) = Purchase();
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.CancelUnissued(request));
        Assert.Equal("payment_pending", request.State);
        Assert.Equal(order.Id, request.OrderId);
    }

    [Fact]
    public void FulfilledPaymentCannotBeClosedAndReleaseItsClaims()
    {
        var (order, request) = Purchase();
        order.Fulfilled = true;
        order.State = "paid";
        request.State = "fulfilled";
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.CloseUnfulfilled(order, request));
        Assert.Equal("paid", order.State);
        Assert.Equal("fulfilled", request.State);
    }

    [Fact]
    public void UnfulfilledPaymentCannotBeMarkedPaidOrDelivered()
    {
        var (order, request) = Purchase();
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.CompletePayment(order, request, true));
        Assert.False(order.DeliveryConfirmed);
        Assert.Equal("payment_pending", order.State);
        Assert.Equal("payment_pending", request.State);
    }

    [Fact]
    public void TransitionCannotMutateAnotherRequestsFinancialState()
    {
        var (order, _) = Purchase();
        var (_, unrelated) = Purchase();
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.RequireReconciliation(order, unrelated));
        Assert.Equal("payment_pending", order.State);
        Assert.Equal("payment_pending", unrelated.State);
    }

    [Fact]
    public void IssuedCheckoutCannotBeReplacedOrAdoptCredentials()
    {
        var (order, request) = Purchase();
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.IssueCheckout(request, "replacement"));
        Assert.Throws<InvalidOperationException>(() => AccountPurchaseLifecycle.CompleteAccountLink(request, DateTimeOffset.UtcNow));
        Assert.Equal(order.Id, request.OrderId);
        Assert.Equal("payment_pending", request.State);
    }

    [Fact]
    public void SuccessfulRedeliveryPreservesDeliveryAndLegacyCompletionMetadata()
    {
        var (order, request) = Purchase();
        order.Fulfilled = true;
        order.DeliveryConfirmed = true;
        // Older fulfilled requests may lack the optional completion-kind field.
        AccountPurchaseLifecycle.CompletePayment(order, request, false);
        Assert.True(order.DeliveryConfirmed);
        Assert.Equal("paid", order.State);
        Assert.Equal("fulfilled", request.State);
        Assert.Null(request.CompletionKind);
    }

    private static (AccountPaymentOrderEntity Order, AccountPurchaseRequestEntity Request) Purchase()
    {
        var request = new AccountPurchaseRequestEntity
        {
            Id = Guid.NewGuid(),
            AccountId = Guid.NewGuid(),
            Purpose = "registration",
            State = "payment_pending",
            OrderId = Guid.NewGuid().ToString("N")
        };
        return (new AccountPaymentOrderEntity
        {
            Id = request.OrderId,
            RequestId = request.Id,
            AccountId = request.AccountId,
            OpenId = "payer",
            PackageId = "cny-9-9",
            PriceFen = 990,
            State = "payment_pending",
            PaymentParametersEncrypted = "retained"
        }, request);
    }
}
