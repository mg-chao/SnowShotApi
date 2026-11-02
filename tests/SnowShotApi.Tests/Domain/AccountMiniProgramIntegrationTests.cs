using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using SnowShot.Application;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task TemporaryAccountFulfillmentRecoversLostCommitWithoutRegeneratingIdentityOrCredits()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault); var mini = await Mini(f);
        var request = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var order = await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token);
        f.Platform.OrderQueried = fault.Arm;
        await PayDirect(f, mini, order, 990);
        var fulfilled = await f.Service.PublicRequestAsync(request.RequestId, mini, Token);
        Assert.Equal("fulfilled", fulfilled.State);
        Assert.Matches("^wx_[a-f0-9]{24}$", fulfilled.Username!);
        await ReleaseQueryCooldown(f, order.OrderId);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        Assert.Equal(fulfilled.Username, (await f.Service.PublicRequestAsync(request.RequestId, mini, Token)).Username);
        await using var db = new SnowShotDbContext(f.Options);
        var id = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        Assert.Equal(9900000000L, await db.AccountWallets.Where(x => x.AccountId == id).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.Single(await db.AccountFinancialEntries.Where(x => x.AccountId == id).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDesktopRegistrationsCannotAdoptTheSameTemporaryAccountTwice()
    {
        var f = Fixture(); var mini = await Mini(f);
        var direct = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        await PayDirect(f, mini, await f.Service.CreateOrderAsync(mini, direct.RequestId, "cny-9-9", Token), 990);
        var registrations = new[] { await Register(f, Name()), await Register(f, Name()) };
        var attempts = await Task.WhenAll(registrations.Select(async registration =>
        {
            try { return await f.Service.LinkRegistrationAsync(mini, registration.RequestId, Token); }
            catch (AccountException error) when (error.Code == "wechat_link_conflict") { return null; }
        }));
        var completed = Assert.Single(attempts, x => x is not null)!;
        Assert.Equal("account_link", completed.CompletionKind);
        await using var db = new SnowShotDbContext(f.Options);
        var id = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        Assert.Single(await db.AccountUsernameClaims.Where(x => x.AccountId == id).ToListAsync(Token));
        Assert.Equal(9900000000L, await db.AccountWallets.Where(x => x.AccountId == id).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DirectWechatPurchaseCreatesOneTemporaryAccountOnlyAfterVerifiedFulfillment()
    {
        var f = Fixture();
        var tokens = await f.Service.WechatLoginAsync("openid-" + Guid.NewGuid().ToString("N"), Name(), Token);
        var mini = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        f.Platform.CodeGenerated = () => throw new InvalidOperationException("Direct purchases must not generate QR codes.");
        var requests = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.MiniProgramPurchaseAsync(mini, Token)));
        Assert.Single(requests.Select(x => x.RequestId).Distinct());
        var request = requests[0];
        Assert.Null(request.Username);
        Assert.Null(request.MiniProgramCodeDataUrl);
        Assert.Equal("mini_registration", request.Purpose);
        Assert.False((await f.Service.SummaryAsync(mini, Token)).Linked);
        var order = await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token);
        var other = await Mini(f);
        Assert.Equal("wechat_link_conflict", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.CreateOrderAsync(other, request.RequestId, "cny-9-9", Token))).Code);
        Assert.Null((await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token)).PaymentParameters);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.VerifyCallbackAsync("", "", "", null,
            DeliveryPayload(order.OrderId, mini.OpenId!, "wx-" + order.OrderId), "", Token)));
        var resolved = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        var summary = await f.Service.SummaryAsync(resolved, Token);
        Assert.True(summary.Linked);
        Assert.True(summary.IsTemporary);
        Assert.Matches("^wx_[a-f0-9]{24}$", summary.Username!);
        Assert.Equal("9900000000", summary.AvailableCreditNanoYuan);
        Assert.Equal("9900000000", summary.CumulativeRechargeNanoYuan);
        Assert.Equal("payment", (await f.Service.PublicRequestAsync(request.RequestId, resolved, Token)).CompletionKind);
        Assert.Equal("invalid_credentials", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.LoginAsync(summary.Username!, "password-123", Name(), Token))).Code);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountFinancialEntries.Where(x => x.AccountId == summary.AccountId).ToListAsync(Token));
        Assert.Single(await db.AccountUsernameClaims.Where(x => x.AccountId == summary.AccountId).ToListAsync(Token));
        Assert.Equal(summary.AccountId, (await f.Service.RefreshSessionAsync(tokens.RefreshToken, Token)).AccountId);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DirectPurchasesAndLinkingRecoverLostCommitAcknowledgementsAndPreserveFinancialOwnership()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault); var mini = await Mini(f);
        fault.Arm();
        var first = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var original = await f.Service.CreateOrderAsync(mini, first.RequestId, "cny-9-9", Token);
        await PayDirect(f, mini, original, 990);
        await using var db = new SnowShotDbContext(f.Options);
        var originalId = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        var linkedMini = mini with { AccountId = originalId };
        var before = await f.Service.SummaryAsync(linkedMini, Token);
        var topUp = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        Assert.Equal("top_up", topUp.Purpose);
        var pending = await f.Service.CreateOrderAsync(mini, topUp.RequestId, "cny-29-9", Token);
        var registration = await Register(f, Name());
        fault.Arm();
        var linked = await f.Service.LinkRegistrationAsync(mini, registration.RequestId, Token);
        Assert.Equal("fulfilled", linked.State);
        Assert.Equal("account_link", linked.CompletionKind);
        Assert.Null(linked.OrderId);
        Assert.Equal(linked, (await f.Service.LinkRegistrationAsync(mini, registration.RequestId, Token)) with { ServerTime = linked.ServerTime });
        var claimed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        Assert.Equal(originalId, claimed.AccountId);
        var after = await f.Service.SummaryAsync((await f.Service.ResolveAsync(claimed.AccessToken, Token))!, Token);
        Assert.False(after.IsTemporary);
        Assert.Equal(registration.Username, after.Username);
        Assert.Equal(before.AvailableCreditNanoYuan, after.AvailableCreditNanoYuan);
        Assert.Equal(before.CumulativeRechargeNanoYuan, after.CumulativeRechargeNanoYuan);
        Assert.Equal(before.MembershipTier, after.MembershipTier);
        Assert.Equal(originalId, (await f.Service.LoginAsync(registration.Username!, "password-123", Name(), Token)).AccountId);
        Assert.Equal("invalid_credentials", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.LoginAsync(before.Username!, "password-123", Name(), Token))).Code);
        await PayDirect(f, mini, pending, 2990);
        // A delayed original-payment query and refund still belong to the same
        // account after credentials are adopted; no financial records move.
        var refundId = Guid.NewGuid().ToString("N");
        f.Platform.Orders[refundId] = new(refundId, mini.OpenId!, 0, "refund", "wx-" + refundId, [new(refundId, 990, "completed")]);
        await ReleaseQueryCooldown(f, original.OrderId);
        await f.Service.RefreshOrderAsync(mini, original.OrderId, Token);
        var refund = JsonSerializer.Serialize(new
        {
            Event = "xpay_refund_notify",
            MchOrderId = original.OrderId,
            WxOrderId = "wx-" + original.OrderId,
            MchRefundId = refundId,
            WxRefundId = "wx-" + refundId,
            RefundFee = 990,
            RetCode = 0,
            OpenId = mini.OpenId
        });
        await f.Service.VerifyCallbackAsync("", "", "", null, refund, "", Token);
        await f.Service.ReconcileAsync(Token);
        var final = await f.Service.SummaryAsync(linkedMini, Token);
        Assert.Equal("29900000000", final.AvailableCreditNanoYuan);
        Assert.Equal("29900000000", final.CumulativeRechargeNanoYuan);
        Assert.All(await db.AccountPaymentOrders.Where(x => x.OpenId == mini.OpenId).ToListAsync(Token), x => Assert.Equal(originalId, x.AccountId));
        Assert.Equal(2, await db.AccountFinancialEntries.CountAsync(x => x.AccountId == originalId && x.Type == "top_up", Token));
        Assert.Equal(originalId, await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).Select(x => x.AccountId).SingleAsync(Token));
        var normalized = SnowShot.Domain.AccountRules.NormalizeUsername(after.Username!);
        Assert.Equal(originalId, await db.AccountUsernameClaims.Where(x => x.NormalizedUsername == normalized).Select(x => x.AccountId).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DirectPurchaseExpirationAndDefinitiveClosureReleaseOnlyUnpaidReservations()
    {
        var f = Fixture(); var mini = await Mini(f);
        var first = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        await using var db = new SnowShotDbContext(f.Options);
        await db.AccountPurchaseRequests.Where(x => x.Id == first.RequestId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        var second = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.Equal("expired", (await f.Service.PublicRequestAsync(first.RequestId, mini, Token)).State);
        var order = await f.Service.CreateOrderAsync(mini, second.RequestId, "cny-9-9", Token);
        await db.AccountPurchaseRequests.Where(x => x.Id == second.RequestId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        f.Platform.FailQueries = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Service.RefreshOrderAsync(mini, order.OrderId, Token));
        Assert.Equal(second.RequestId, (await f.Service.MiniProgramPurchaseAsync(mini, Token)).RequestId);
        Assert.False((await f.Service.SummaryAsync(mini, Token)).Linked);
        f.Platform.FailQueries = false;
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 0, "closed", null, []);
        await ReleaseQueryCooldown(f, order.OrderId);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        Assert.Equal("failed", (await f.Service.PublicRequestAsync(second.RequestId, mini, Token)).State);
        var third = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        Assert.NotEqual(second.RequestId, third.RequestId);
        Assert.False(await db.AccountWechatLinks.AnyAsync(x => x.OpenId == mini.OpenId, Token));
        Assert.False(await db.Accounts.AnyAsync(x => x.Id == db.AccountPurchaseRequests.Where(r => r.Id == second.RequestId).Select(r => r.AccountId).First() && x.Active, Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LinkingRejectsExpiredIssuedAndAlreadyRegularAccountsWithoutChangingOwnership()
    {
        var f = Fixture(); var mini = await Mini(f);
        var direct = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var order = await f.Service.CreateOrderAsync(mini, direct.RequestId, "cny-9-9", Token);
        await PayDirect(f, mini, order, 990);
        var issued = await Register(f, Name()); var other = await Mini(f);
        await f.Service.CreateOrderAsync(other, issued.RequestId, "cny-9-9", Token);
        Assert.Equal("payment_pending", (await Assert.ThrowsAsync<AccountException>(() => f.Service.LinkRegistrationAsync(mini, issued.RequestId, Token))).Code);
        var expired = await Register(f, Name());
        await using var db = new SnowShotDbContext(f.Options);
        await db.AccountPurchaseRequests.Where(x => x.Id == expired.RequestId).ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        Assert.Equal("request_expired", (await Assert.ThrowsAsync<AccountException>(() => f.Service.LinkRegistrationAsync(mini, expired.RequestId, Token))).Code);
        var registration = await Register(f, Name());
        var unchanged = await f.Service.LinkRegistrationAsync(other, registration.RequestId, Token);
        Assert.Equal("awaiting_payment", unchanged.State);
        var links = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Service.LinkRegistrationAsync(mini, registration.RequestId, Token)));
        Assert.All(links, x => Assert.Equal("account_link", x.CompletionKind));
        Assert.Equal("wechat_link_conflict", (await Assert.ThrowsAsync<AccountException>(() => f.Service.LinkRegistrationAsync(other, registration.RequestId, Token))).Code);
        var replacement = await Register(f, Name());
        Assert.Equal("wechat_link_conflict", (await Assert.ThrowsAsync<AccountException>(() => f.Service.LinkRegistrationAsync(mini, replacement.RequestId, Token))).Code);
        Assert.Equal("invalid_session", (await Assert.ThrowsAsync<AccountException>(() => f.Service.LinkRegistrationAsync(new(Guid.NewGuid(), null, null), replacement.RequestId, Token))).Code);
    }

    private static async Task PayDirect(TestFixture f, AccountSessionIdentity mini, AccountPurchaseOrder order, int fen)
    {
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, fen, "paid", "wx-" + order.OrderId, [], fen);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
    }
}
