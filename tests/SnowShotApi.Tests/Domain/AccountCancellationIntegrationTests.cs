using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelTopUpInvalidatesUnissuedCodeAndIsIdempotent(bool paymentsDisabled)
    {
        var fault = new LostCommitAcknowledgement();
        var (f, desktop, mini, request) = await CancellationFixture(fault);
        var before = await f.Service.SummaryAsync(desktop, Token);
        f.Platform.PaymentsAvailable = !paymentsDisabled;
        fault.Arm();
        var canceled = await f.Service.CancelTopUpAsync(desktop, request.RequestId, Token);
        var repeated = await f.Service.CancelTopUpAsync(desktop, request.RequestId, Token);
        Assert.Equal("failed", canceled.State);
        Assert.Equal("failed", repeated.State);
        Assert.Null(canceled.OrderId);
        Assert.Equal("failed", (await f.Service.PublicRequestAsync(request.RequestId, mini, Token)).State);
        await using (var db = new SnowShotDbContext(f.Options))
        {
            var stored = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == request.RequestId, Token);
            Assert.Null(stored.CodeImage);
            Assert.False(await db.AccountPaymentOrders.AnyAsync(x => x.RequestId == request.RequestId, Token));
        }
        var after = await f.Service.SummaryAsync(desktop, Token);
        Assert.Equal(before.AvailableCreditNanoYuan, after.AvailableCreditNanoYuan);
        Assert.Equal(before.CumulativeRechargeNanoYuan, after.CumulativeRechargeNanoYuan);
        f.Platform.PaymentsAvailable = true;
        var error = await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token));
        Assert.Equal("payment_pending", error.Code);
        var replacement = await f.Service.TopUpAsync(desktop, Token);
        Assert.NotEqual(request.RequestId, replacement.RequestId);
        Assert.Equal("awaiting_payment", replacement.State);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CancelTopUpRequiresOwningLiveAccountAndCannotCancelRegistration()
    {
        var (f, desktop, _, request) = await CancellationFixture();
        var other = await Mini(f);
        var foreign = await Assert.ThrowsAsync<AccountException>(() => f.Service.CancelTopUpAsync(other, request.RequestId, Token));
        Assert.Equal(404, foreign.Status);
        var registration = await Register(f, Name());
        var wrongPurpose = await Assert.ThrowsAsync<AccountException>(() => f.Service.CancelTopUpAsync(desktop, registration.RequestId, Token));
        Assert.Equal(404, wrongPurpose.Status);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountSessions.Where(x => x.Id == desktop.SessionId).ExecuteUpdateAsync(set =>
                set.SetProperty(x => x.RevokedAt, DateTimeOffset.UtcNow), Token);
        var revoked = await Assert.ThrowsAsync<AccountException>(() => f.Service.CancelTopUpAsync(desktop, request.RequestId, Token));
        Assert.Equal("invalid_session", revoked.Code);
        await using var verify = new SnowShotDbContext(f.Options);
        Assert.Equal("awaiting_payment", await verify.AccountPurchaseRequests.Where(x => x.Id == request.RequestId).Select(x => x.State).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CancelTopUpPreservesIssuedPaymentsAndTheirLaterFulfillment()
    {
        var (f, desktop, mini, request) = await CancellationFixture();
        var order = await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token);
        var pending = await f.Service.CancelTopUpAsync(desktop, request.RequestId, Token);
        Assert.Equal("payment_pending", pending.State);
        Assert.Equal(order.OrderId, pending.OrderId);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var completed = await f.Service.CancelTopUpAsync(desktop, request.RequestId, Token);
        Assert.Equal("fulfilled", completed.State);
        Assert.Equal("19800000000", (await f.Service.SummaryAsync(desktop, Token)).AvailableCreditNanoYuan);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CancelTopUpAndCheckoutSerializeWithoutDiscardingAnIssuedOrder()
    {
        var (f, desktop, mini, request) = await CancellationFixture();
        async Task<AccountPurchaseOrder?> Checkout()
        {
            try { return await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token); }
            catch (AccountException error) when (error.Code == "payment_pending") { return null; }
        }
        var checkout = Checkout();
        var cancellation = f.Service.CancelTopUpAsync(desktop, request.RequestId, Token);
        await Task.WhenAll(checkout, cancellation);
        await using var db = new SnowShotDbContext(f.Options);
        var stored = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == request.RequestId, Token);
        var issued = await checkout;
        if (issued is null)
        {
            Assert.Equal("failed", stored.State);
            Assert.Null(stored.OrderId);
            Assert.False(await db.AccountPaymentOrders.AnyAsync(x => x.RequestId == request.RequestId, Token));
        }
        else
        {
            Assert.Equal("payment_pending", stored.State);
            Assert.Equal(issued.OrderId, stored.OrderId);
            Assert.Single(await db.AccountPaymentOrders.Where(x => x.RequestId == request.RequestId).ToListAsync(Token));
        }
    }

    private static async Task<(TestFixture Fixture, AccountSessionIdentity Desktop, AccountSessionIdentity Mini, AccountPurchaseRequest Request)>
        CancellationFixture(LostCommitAcknowledgement? fault = null)
    {
        var f = Fixture(fault); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var tokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        mini = mini with { AccountId = desktop.AccountId };
        return (f, desktop, mini, await f.Service.TopUpAsync(desktop, Token));
    }
}
