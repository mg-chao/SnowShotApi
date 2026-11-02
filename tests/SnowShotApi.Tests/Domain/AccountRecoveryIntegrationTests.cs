using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableOlderRecordsCannotStarveNewerRefundsOrDeliveries(bool delivery)
    {
        var f = Fixture();
        var ids = await SeedRecoveryAsync(f, delivery, 21);
        foreach (var id in ids.Take(20)) f.Platform.UnavailableDeliveries.Add(id);
        var newest = ids[^1];
        if (!delivery)
            f.Platform.Orders[newest] = new(newest, "recovery", 0, "refund", "wx-" + newest, [new(newest, 1, "completed")]);
        await f.Service.ReconcileAsync(Token);
        await f.Service.ReconcileAsync(Token);
        await using var db = new SnowShotDbContext(f.Options);
        if (delivery)
            Assert.True(await db.AccountPaymentOrders.Where(x => x.Id == newest).Select(x => x.DeliveryConfirmed).SingleAsync(Token));
        else
            Assert.True(await db.AccountRefunds.Where(x => x.Id == newest).Select(x => x.Applied).SingleAsync(Token));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetryLeasesExcludeReplicasRecoverCrashesAndFenceStaleOwners(bool delivery)
    {
        var f = Fixture();
        var id = (await SeedRecoveryAsync(f, delivery, 1))[0];
        var kind = delivery ? AccountRetryKind.Delivery : AccountRetryKind.Refund;
        var queue = new PostgresAccountRetryQueue(new DistributedIntegrationTests.ContextFactory(f.Options));
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => queue.ClaimAsync(kind, id, Token)));
        var first = Assert.Single(claims, x => x is not null)!;
        await using var db = new SnowShotDbContext(f.Options);
        var past = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (delivery)
            await db.AccountPaymentOrders.Where(x => x.Id == id).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.DeliveryLeaseExpiresAt, past).SetProperty(x => x.NextDeliveryAttemptAt, past), Token);
        else
            await db.AccountRefunds.Where(x => x.Id == id).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.QueryLeaseExpiresAt, past).SetProperty(x => x.NextQueryAt, past), Token);
        var second = Assert.IsType<AccountRetryLease>(await queue.ClaimAsync(kind, id, Token));
        Assert.NotEqual(first.Token, second.Token);
        Assert.Equal(2, second.Attempt);
        await queue.ReleaseAsync(first, Token);
        var retainedToken = delivery
            ? await db.AccountPaymentOrders.Where(x => x.Id == id).Select(x => x.DeliveryLeaseToken).SingleAsync(Token)
            : await db.AccountRefunds.Where(x => x.Id == id).Select(x => x.QueryLeaseToken).SingleAsync(Token);
        Assert.Equal(second.Token, retainedToken);
        await queue.ReleaseAsync(second, Token);
        Assert.Null(await queue.ClaimAsync(kind, id, Token));
        var nextAttempt = delivery
            ? await db.AccountPaymentOrders.Where(x => x.Id == id).Select(x => x.NextDeliveryAttemptAt).SingleAsync(Token)
            : await db.AccountRefunds.Where(x => x.Id == id).Select(x => x.NextQueryAt).SingleAsync(Token);
        Assert.True(nextAttempt > DateTimeOffset.UtcNow);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RoutineAccountOperationsProceedDuringAnotherAccountTransaction()
    {
        var f = Fixture();
        var registration = await Register(f, Name());
        var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, mini.OpenId!, "wx-" + order.OrderId), "", Token);
        var desktopTokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(desktopTokens.AccessToken, Token))!;

        await using var other = new SnowShotDbContext(f.Options);
        await using var transaction = await other.Database.BeginTransactionAsync(Token);
        await AccountSynchronization.ReadIdentityAsync(other, Token);
        await AccountSynchronization.AccountAsync(other, Guid.NewGuid(), Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        var token = deadline.Token;
        await Task.WhenAll(
            f.Service.LoginAsync(registration.Username!, "password-123", Guid.NewGuid().ToString("N"), token),
            f.Service.WechatLoginAsync(mini.OpenId!, Guid.NewGuid().ToString("N"), token),
            f.Service.RefreshSessionAsync(desktopTokens.RefreshToken, token),
            f.Service.PublicRequestAsync(registration.RequestId, mini, token),
            f.Service.TopUpAsync(desktop, token),
            f.Service.DeletionEligibilityAsync(mini, token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentTopUpDeliveriesCreditEachOrderOnceWithoutAnExclusiveIdentityLock()
    {
        var f = Fixture();
        var registration = await Register(f, Name());
        var mini = await Mini(f);
        var first = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(first.OrderId, mini.OpenId!, "wx-" + first.OrderId), "", Token);
        var tokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        var orderIds = new List<string>();
        for (var index = 0; index < 2; index++)
        {
            var request = await f.Service.TopUpAsync(desktop, Token);
            orderIds.Add((await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token)).OrderId);
        }
        await using var other = new SnowShotDbContext(f.Options);
        await using var transaction = await other.Database.BeginTransactionAsync(Token);
        await AccountSynchronization.ReadIdentityAsync(other, Token);
        await AccountSynchronization.AccountAsync(other, Guid.NewGuid(), Token);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        await Task.WhenAll(orderIds.SelectMany(id => Enumerable.Range(0, 3).Select(_ =>
            f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(id, mini.OpenId!, "wx-" + id), "", deadline.Token))));
        var summary = await f.Service.SummaryAsync(desktop, Token);
        Assert.Equal("29700000000", summary.AvailableCreditNanoYuan);
        Assert.Equal("29700000000", summary.CumulativeRechargeNanoYuan);
        var entries = await f.Service.TransactionsAsync(desktop, null, 20, Token);
        Assert.Equal(3, entries.Items.Count);
        Assert.All(entries.Items, entry => Assert.Equal("completed", entry.Status));
    }

    private static async Task<string[]> SeedRecoveryAsync(TestFixture f, bool delivery, int count)
    {
        var accountId = Guid.NewGuid();
        var name = Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        await using var db = new SnowShotDbContext(f.Options);
        db.Accounts.Add(new() { Id = accountId, Username = name, NormalizedUsername = name.ToUpperInvariant(), PasswordHash = "", Active = true, CreatedAt = now });
        db.AccountWallets.Add(new() { AccountId = accountId, AvailableNanoYuan = 100000000, CumulativeRechargeNanoYuan = 100000000 });
        for (var index = 0; index < count; index++)
        {
            var id = ids[index];
            var requestId = Guid.NewGuid();
            var created = now.AddDays(-1).AddSeconds(index);
            db.AccountPurchaseRequests.Add(new() { Id = requestId, AccountId = accountId, Purpose = "top_up", State = "fulfilled", GeneratedAt = created, ExpiresAt = created, OrderId = id });
            db.AccountPaymentOrders.Add(new() { Id = id, RequestId = requestId, AccountId = accountId, OpenId = "recovery", PackageId = "cny-0-1", PriceFen = 10, PaidFen = 10, State = "paid", Fulfilled = true, DeliveryConfirmed = !delivery, PaymentParametersEncrypted = "", CreatedAt = created });
            if (!delivery)
                db.AccountRefunds.Add(new() { Id = id, OrderId = id, RefundFen = 1, State = "processing", SuccessNotified = true, PlatformRefundId = "wx-" + id, UpdatedAt = created });
        }
        await db.SaveChangesAsync(Token);
        return ids;
    }
}
