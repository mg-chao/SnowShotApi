using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Configuration;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

[Collection(DistributedIntegrationGroup.Name)]
public sealed partial class AccountLifecycleIntegrationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact, Trait("Category", "Integration")]
    public async Task TestCardCreditsOnlyItsVerifiedTenFenPayment()
    {
        var f = Fixture(allowPaymentTesting: true); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-0-1", Token);
        using var sign = JsonDocument.Parse(order.PaymentParameters!.SignData);
        Assert.Equal(10, sign.RootElement.GetProperty("goodsPrice").GetInt32());
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 10, "paid", "wx-" + order.OrderId, [], 10);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var claimed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var identity = (await f.Service.ResolveAsync(claimed.AccessToken, Token))!;
        var summary = await f.Service.SummaryAsync(identity, Token);
        Assert.Equal("100000000", summary.AvailableCreditNanoYuan);
        Assert.Equal("100000000", summary.CumulativeRechargeNanoYuan);
        Assert.Null(summary.MembershipTier);
        Assert.False(summary.FreeTableExtraction);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDeliveriesCreditOnceWithoutDependingOnQueries()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.FailQueries = true;
        var payload = DeliveryPayload(order.OrderId, mini.OpenId!, "wx-" + order.OrderId);
        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token)));
        Assert.All(responses, response => Assert.Contains("\"ErrCode\":0", response, StringComparison.Ordinal));
        // A different serialization is another inbox record for the same platform
        // order, and must still be financially idempotent.
        await f.Service.VerifyCallbackAsync("", "", "", null, " " + payload, "", Token);
        var claimed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var identity = (await f.Service.ResolveAsync(claimed.AccessToken, Token))!;
        Assert.Equal("9900000000", (await f.Service.SummaryAsync(identity, Token)).AvailableCreditNanoYuan);
        Assert.Equal("9900000000", (await f.Service.SummaryAsync(identity, Token)).CumulativeRechargeNanoYuan);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountFinancialEntries.Where(x => x.AccountId == identity.AccountId).ToListAsync(Token));
        Assert.True(await db.AccountPaymentOrders.Where(x => x.Id == order.OrderId).Select(x => x.DeliveryConfirmed).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PlatformOrderCannotCreditTwoMerchantOrders()
    {
        var f = Fixture(); var first = await Register(f, Name()); var payer = await Mini(f);
        var order = await f.Service.CreateOrderAsync(payer, first.RequestId, "cny-9-9", Token);
        var platformId = "wx-" + order.OrderId;
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, payer.OpenId!, platformId), "", Token);
        var second = await Register(f, Name()); var secondPayer = await Mini(f);
        var secondOrder = await f.Service.CreateOrderAsync(secondPayer, second.RequestId, "cny-9-9", Token);
        await Assert.ThrowsAsync<AccountException>(() => f.Service.VerifyCallbackAsync("", "", "", null,
            DeliveryPayload(secondOrder.OrderId, secondPayer.OpenId!, platformId), "", Token));
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Equal("reconciliation_required", await db.AccountPaymentOrders.Where(x => x.Id == secondOrder.OrderId).Select(x => x.State).SingleAsync(Token));
        Assert.False(await db.AccountPaymentOrders.Where(x => x.Id == secondOrder.OrderId).Select(x => x.Fulfilled).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RepeatedCheckoutDoesNotReissueMerchantOrderForPayment()
    {
        var f = Fixture(); var request = await Register(f, Name()); var payer = await Mini(f);
        var first = await f.Service.CreateOrderAsync(payer, request.RequestId, "cny-9-9", Token);
        var repeated = await f.Service.CreateOrderAsync(payer, request.RequestId, "cny-9-9", Token);
        Assert.Matches("^[a-f0-9]{32}$", first.OrderId);
        Assert.NotNull(first.PaymentParameters);
        Assert.Equal(first.OrderId, repeated.OrderId);
        Assert.Null(repeated.PaymentParameters);
    }

    private static string DeliveryPayload(string orderId, string openId, string platformId, string productId = "cny-9-9", int paid = 990) => JsonSerializer.Serialize(new
    {
        Event = "xpay_goods_deliver_notify",
        OpenId = openId,
        OutTradeNo = orderId,
        Env = 0,
        WeChatPayInfo = new { MchOrderNo = platformId },
        GoodsInfo = new { ProductId = productId, Quantity = 1, OrigPrice = paid, ActualPrice = paid, Attach = orderId }
    });

    [Fact, Trait("Category", "Integration")]
    public async Task LostRegistrationCommitAcknowledgementRecoversInitiatorProof()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault);
        f.Platform.CodeGenerated = fault.Arm;
        var request = await Register(f, Name());
        Assert.False(string.IsNullOrWhiteSpace(request.RegistrationProof));
        var recovered = await f.Service.RegistrationAsync(request.RequestId, request.RegistrationProof!, false, Token);
        Assert.Equal("awaiting_payment", recovered.State);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountPurchaseRequests.Where(x => x.Id == request.RequestId).ToListAsync(Token));
        Assert.Single(await db.AccountUsernameClaims.Where(x => x.RequestId == request.RequestId).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LostCaptchaValidationAcknowledgementDoesNotConsumeSubmission()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault);
        var request = await Register(f, Name(), fault.Arm);
        Assert.Equal("awaiting_payment", request.State);
        Assert.False(string.IsNullOrWhiteSpace(request.RegistrationProof));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LostRefreshCommitAcknowledgementRecoversExactlyTheRotatedSession()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault);
        var initial = await f.Service.WechatLoginAsync("openid-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Token);
        var original = (await f.Service.ResolveAsync(initial.AccessToken, Token))!;
        fault.Arm();
        var rotated = await f.Service.RefreshSessionAsync(initial.RefreshToken, Token);
        Assert.NotEqual(initial.RefreshToken, rotated.RefreshToken);
        Assert.Equal(original.SessionId, (await f.Service.ResolveAsync(rotated.AccessToken, Token))!.SessionId);
        await Assert.ThrowsAsync<AccountException>(() => f.Service.RefreshSessionAsync(initial.RefreshToken, Token));
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Equal(AccountSecrets.Hash(rotated.RefreshToken), (await db.AccountSessions.SingleAsync(x => x.Id == original.SessionId, Token)).RefreshHash);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LostTopUpRequestAcknowledgementDoesNotCreateAnotherRequest()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault);
        var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var tokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        f.Platform.CodeGenerated = fault.Arm;
        var request = await f.Service.TopUpAsync(desktop, Token);
        Assert.Equal("top_up", request.Purpose);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountPurchaseRequests.Where(x => x.AccountId == desktop.AccountId && x.Purpose == "top_up").ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UsernameClaimIsCaseInsensitiveAndExpiredCodeCannotOrder()
    {
        var f = Fixture(); var username = Name();
        var first = await Register(f, username);
        var conflict = await Assert.ThrowsAsync<AccountException>(() => Register(f, username.ToUpperInvariant()));
        Assert.Equal("username_unavailable", conflict.Code);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == first.RequestId).ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Token);
        var replacement = await Register(f, username);
        Assert.NotEqual(first.RequestId, replacement.RequestId);
        var mini = await Mini(f);
        var expired = await Assert.ThrowsAsync<AccountException>(() => f.Service.CreateOrderAsync(mini, first.RequestId, "cny-9-9", Token));
        Assert.Equal("request_expired", expired.Code);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExpiredRegistrationLatePaymentCreditsTemporaryAccountWhenUsernameWasReservedAgain()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var originalPayer = await Mini(f);
        var attemptId = Guid.NewGuid();
        var order = await f.Service.CreateOrderAsync(originalPayer, registration.RequestId, "cny-9-9", Token, attemptId);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)), Token);

        var replacement = await Register(f, registration.Username!);
        Assert.NotEqual(registration.RequestId, replacement.RequestId);
        var replacementPayer = await Mini(f);
        var replacementOrder = await f.Service.CreateOrderAsync(replacementPayer, replacement.RequestId, "cny-9-9", Token);
        var replay = await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.CreateOrderAsync(originalPayer, registration.RequestId, "cny-9-9", Token, attemptId));
        Assert.Equal("request_expired", replay.Code);
        var retained = await f.Service.PublicRequestAsync(registration.RequestId, originalPayer, Token);
        Assert.Equal("expired", retained.State);
        Assert.Equal("payment_pending", retained.OrderState);
        Assert.Equal(order.OrderId, retained.OrderId);
        var otherView = await f.Service.PublicRequestAsync(registration.RequestId, replacementPayer, Token);
        Assert.Null(otherView.OrderId);
        Assert.Null(otherView.OrderState);
        f.Platform.FailQueries = true;
        await Assert.ThrowsAsync<HttpRequestException>(() => f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, true, Token));
        f.Platform.FailQueries = false;
        var payload = DeliveryPayload(order.OrderId, originalPayer.OpenId!, "wx-" + order.OrderId);
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, " " + payload, "", Token);

        var status = await f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, false, Token);
        Assert.Equal("expired", status.State);
        Assert.Equal(order.OrderId, status.OrderId);
        Assert.Equal("paid", (await f.Service.PublicRequestAsync(registration.RequestId, originalPayer, Token)).OrderState);
        var claim = await Assert.ThrowsAsync<AccountException>(() => f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token));
        Assert.Equal("payment_not_received", claim.Code);

        await f.Service.VerifyCallbackAsync("", "", "", null,
            DeliveryPayload(replacementOrder.OrderId, replacementPayer.OpenId!, "wx-" + replacementOrder.OrderId), "", Token);
        var replacementSession = await f.Service.ClaimAsync(replacement.RequestId, replacement.RegistrationProof!, Token);
        var normalizedUsername = registration.Username!.ToUpperInvariant();
        await using var read = new SnowShotDbContext(f.Options);
        var originalAccountId = await read.AccountWechatLinks.Where(x => x.OpenId == originalPayer.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        var originalAccount = await read.Accounts.SingleAsync(x => x.Id == originalAccountId, Token);
        Assert.True(originalAccount.IsTemporary);
        Assert.StartsWith("wx_", originalAccount.Username);
        Assert.Empty(originalAccount.PasswordHash);
        Assert.NotEqual(replacementSession.AccountId, originalAccountId);
        Assert.Equal(originalAccountId, await read.AccountPaymentOrders.Where(x => x.Id == order.OrderId && x.State == "paid")
            .Select(x => x.AccountId).SingleAsync(Token));
        Assert.Equal(replacementSession.AccountId, await read.AccountUsernameClaims.Where(x => x.NormalizedUsername == normalizedUsername)
            .Select(x => x.AccountId).SingleAsync(Token));
        Assert.Equal(9_900_000_000L, await read.AccountWallets.Where(x => x.AccountId == originalAccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.Equal(9_900_000_000L, await read.AccountWallets.Where(x => x.AccountId == replacementSession.AccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task RegistrationClosurePreservesLatePaymentRecovery(bool closedBeforeDeadline)
    {
        var f = Fixture();
        var username = Name();
        var registration = await Register(f, username);
        var payer = await Mini(f);
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        await using var db = new SnowShotDbContext(f.Options);
        var originalRequest = db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId);
        if (!closedBeforeDeadline)
            await originalRequest.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);

        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 0, "closed", null, [], 990);
        Assert.Equal("closed", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        var status = await f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, false, Token);
        Assert.Equal(closedBeforeDeadline ? "failed" : "expired", status.State);
        Assert.Equal("closed", status.OrderState);
        Assert.Equal(order.OrderId, status.OrderId);
        var publicStatus = await f.Service.PublicRequestAsync(registration.RequestId, payer, Token);
        Assert.Equal(status.State, publicStatus.State);
        Assert.Equal("closed", publicStatus.OrderState);
        Assert.Equal(order.OrderId, publicStatus.OrderId);
        var otherStatus = await f.Service.PublicRequestAsync(registration.RequestId, await Mini(f), Token);
        Assert.Null(otherStatus.OrderId);
        Assert.Null(otherStatus.OrderState);

        var replacement = await Register(f, username);
        if (closedBeforeDeadline)
            await originalRequest.ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        await ReleaseQueryCooldown(f, order.OrderId);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990, Delivered: true);
        Assert.Equal("paid", (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        var payerStatus = await f.Service.PublicRequestAsync(registration.RequestId, payer, Token);
        Assert.Equal("expired", payerStatus.State);
        Assert.Equal("paid", payerStatus.OrderState);
        var account = await (from link in db.AccountWechatLinks
                             join value in db.Accounts on link.AccountId equals value.Id
                             where link.OpenId == payer.OpenId
                             select value).SingleAsync(Token);
        Assert.True(account.IsTemporary);
        Assert.Equal(9_900_000_000L, await db.AccountWallets.Where(x => x.AccountId == account.Id)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.True(await db.AccountUsernameClaims.AnyAsync(x => x.RequestId == replacement.RequestId, Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExpiredRegistrationLatePaymentRestoresAvailableUsernameAndOriginalCredentials()
    {
        var f = Fixture(); var username = Name(); var registration = await Register(f, username); var payer = await Mini(f);
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        Assert.Equal("expired", (await f.Service.PublicRequestAsync(registration.RequestId, payer, Token)).State);
        var expired = await f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, false, Token);
        Assert.Equal(order.OrderId, expired.OrderId);
        Assert.Equal("payment_pending", expired.OrderState);

        var payload = DeliveryPayload(order.OrderId, payer.OpenId!, "wx-" + order.OrderId);
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, " " + payload, "", Token);

        var status = await f.Service.RegistrationAsync(registration.RequestId, registration.RegistrationProof!, false, Token);
        Assert.Equal("fulfilled", status.State);
        Assert.Equal(username, status.Username);
        var claimed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var login = await f.Service.LoginAsync(username, "password-123", Guid.NewGuid().ToString(), Token);
        Assert.Equal(claimed.AccountId, login.AccountId);
        await using var read = new SnowShotDbContext(f.Options);
        Assert.Equal(claimed.AccountId, await read.AccountWechatLinks.Where(x => x.OpenId == payer.OpenId)
            .Select(x => x.AccountId).SingleAsync(Token));
        Assert.Equal(9_900_000_000L, await read.AccountWallets.Where(x => x.AccountId == claimed.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task ExpiredPayerClaimForAnotherUsernameDoesNotBlockLatePayment(bool alreadyMarkedExpired)
    {
        var f = Fixture();
        var payer = await Mini(f);
        var original = await Register(f, Name());
        var order = await f.Service.CreateOrderAsync(payer, original.RequestId, "cny-9-9", Token);
        await using var db = new SnowShotDbContext(f.Options);
        var deadline = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.AccountPurchaseRequests.Where(x => x.Id == original.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, deadline), Token);
        var replacement = await Register(f, Name());
        await f.Service.CreateOrderAsync(payer, replacement.RequestId, "cny-9-9", Token);
        await db.AccountPurchaseRequests.Where(x => x.Id == replacement.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, deadline)
                .SetProperty(x => x.State, alreadyMarkedExpired ? "expired" : "payment_pending"), Token);

        var payload = DeliveryPayload(order.OrderId, payer.OpenId!, "wx-" + order.OrderId);
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        Assert.Equal("fulfilled", (await f.Service.RegistrationAsync(original.RequestId, original.RegistrationProof!, false, Token)).State);
        Assert.Equal("expired", await db.AccountPurchaseRequests.Where(x => x.Id == replacement.RequestId)
            .Select(x => x.State).SingleAsync(Token));
        Assert.False(await db.AccountPayerClaims.AnyAsync(x => x.RequestId == replacement.RequestId, Token));
        Assert.False(await db.AccountUsernameClaims.AnyAsync(x => x.RequestId == replacement.RequestId, Token));
        var claimed = await f.Service.ClaimAsync(original.RequestId, original.RegistrationProof!, Token);
        Assert.Equal(9_900_000_000L, await db.AccountWallets.Where(x => x.AccountId == claimed.AccountId)
            .Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task SameWechatCanPayNewRegistrationAndOlderPaymentRequiresReconciliation(bool previousExpiryKeptPayerClaim)
    {
        var f = Fixture(); var username = Name(); var first = await Register(f, username); var payer = await Mini(f);
        var oldOrder = await f.Service.CreateOrderAsync(payer, first.RequestId, "cny-9-9", Token);
        await using (var db = new SnowShotDbContext(f.Options))
        {
            await db.AccountPurchaseRequests.Where(x => x.Id == first.RequestId).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
            if (previousExpiryKeptPayerClaim)
            {
                await db.AccountPurchaseRequests.Where(x => x.Id == first.RequestId).ExecuteUpdateAsync(s =>
                    s.SetProperty(x => x.State, "expired"), Token);
                await db.AccountUsernameClaims.Where(x => x.RequestId == first.RequestId).ExecuteDeleteAsync(Token);
            }
        }

        var replacement = await Register(f, username);
        var newOrder = await f.Service.CreateOrderAsync(payer, replacement.RequestId, "cny-29-9", Token);
        Assert.NotEqual(oldOrder.OrderId, newOrder.OrderId);
        if (previousExpiryKeptPayerClaim)
            await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(newOrder.OrderId, payer.OpenId!,
                "wx-" + newOrder.OrderId, "cny-29-9", 2990), "", Token);

        f.Platform.Orders[oldOrder.OrderId] = new(oldOrder.OrderId, payer.OpenId!, 990, "paid", "wx-" + oldOrder.OrderId, [], 990);
        var oldStatus = await f.Service.RefreshOrderAsync(payer, oldOrder.OrderId, Token);
        Assert.Equal("reconciliation_required", oldStatus.State);
        Assert.Equal("expired", (await f.Service.PublicRequestAsync(first.RequestId, payer, Token)).State);
        if (!previousExpiryKeptPayerClaim)
            await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(newOrder.OrderId, payer.OpenId!,
                "wx-" + newOrder.OrderId, "cny-29-9", 2990), "", Token);

        var claimed = await f.Service.ClaimAsync(replacement.RequestId, replacement.RegistrationProof!, Token);
        await using var read = new SnowShotDbContext(f.Options);
        var old = await read.AccountPaymentOrders.SingleAsync(x => x.Id == oldOrder.OrderId, Token);
        Assert.False(old.Fulfilled);
        Assert.Equal(990, old.PaidFen);
        Assert.Equal(0L, await read.AccountWallets.Where(x => x.AccountId == old.AccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.Equal(29_900_000_000L, await read.AccountWallets.Where(x => x.AccountId == claimed.AccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.Equal(claimed.AccountId, await read.AccountWechatLinks.Where(x => x.OpenId == payer.OpenId)
            .Select(x => x.AccountId).SingleAsync(Token));
    }

    [Theory, InlineData(false), InlineData(true), Trait("Category", "Integration")]
    public async Task FullRefundOfExpiredRegistrationSettlesWithoutTouchingReplacementAccount(bool paidEvidenceAlreadyRecorded)
    {
        var f = Fixture(); var username = Name(); var first = await Register(f, username); var payer = await Mini(f);
        var oldOrder = await f.Service.CreateOrderAsync(payer, first.RequestId, "cny-9-9", Token);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == first.RequestId).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        var replacement = await Register(f, username);
        var newOrder = await f.Service.CreateOrderAsync(payer, replacement.RequestId, "cny-9-9", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(newOrder.OrderId, payer.OpenId!,
            "wx-" + newOrder.OrderId), "", Token);
        var claimed = await f.Service.ClaimAsync(replacement.RequestId, replacement.RegistrationProof!, Token);
        var transactionId = "wx-" + oldOrder.OrderId;
        f.Platform.Orders[oldOrder.OrderId] = new(oldOrder.OrderId, payer.OpenId!, 990, "paid", transactionId, [], 990);

        if (paidEvidenceAlreadyRecorded)
        {
            Assert.Equal("reconciliation_required", (await f.Service.RefreshOrderAsync(payer, oldOrder.OrderId, Token)).State);
            await SendRefundAsync(490);
            Assert.Equal("reconciliation_required", (await f.Service.RefreshOrderAsync(payer, oldOrder.OrderId, Token)).State);
            Assert.False((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
            await SendRefundAsync(500);
        }
        else await SendRefundAsync(990);

        f.Platform.FailQueries = true;
        var status = await f.Service.RefreshOrderAsync(payer, oldOrder.OrderId, Token);
        Assert.Equal("refunded", status.State);
        Assert.Equal("completed", status.RefundStatus);
        var delivery = await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(oldOrder.OrderId, payer.OpenId!, transactionId), "", Token));
        Assert.Equal("order_refunded", delivery.Code);
        Assert.Equal("refunded", (await f.Service.PublicRequestAsync(first.RequestId, payer, Token)).OrderState);
        Assert.Equal("refunded", (await f.Service.RegistrationAsync(first.RequestId, first.RegistrationProof!, false, Token)).OrderState);
        Assert.True((await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
        await using var read = new SnowShotDbContext(f.Options);
        var old = await read.AccountPaymentOrders.SingleAsync(x => x.Id == oldOrder.OrderId, Token);
        Assert.False(old.Fulfilled);
        Assert.False(await read.Accounts.Where(x => x.Id == old.AccountId).Select(x => x.Active).SingleAsync(Token));
        Assert.Equal(0L, await read.AccountWallets.Where(x => x.AccountId == old.AccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        Assert.Equal(9_900_000_000L, await read.AccountWallets.Where(x => x.AccountId == claimed.AccountId).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
        var refunds = await read.AccountRefunds.Where(x => x.OrderId == oldOrder.OrderId).ToListAsync(Token);
        Assert.Equal(990, refunds.Sum(x => x.RefundFen));
        Assert.All(refunds, refund => Assert.True(refund.Applied));
        var entries = await read.AccountFinancialEntries.Where(x => x.OrderId == oldOrder.OrderId).ToListAsync(Token);
        Assert.Equal(0L, entries.Sum(x => x.AmountNanoYuan));
        Assert.All(entries, entry => Assert.Equal("completed", entry.Status));
        Assert.DoesNotContain(oldOrder.OrderId, f.Platform.DeliveryCalls);

        async Task SendRefundAsync(int fen)
        {
            var refundId = Guid.NewGuid().ToString("N");
            f.Platform.Orders[refundId] = new(refundId, payer.OpenId!, 0, "refund", "wx-" + refundId, [new(refundId, fen, "completed")]);
            await f.Service.VerifyCallbackAsync("", "", "", null, JsonSerializer.Serialize(new
            {
                Event = "xpay_refund_notify",
                MchOrderId = oldOrder.OrderId,
                WxOrderId = transactionId,
                MchRefundId = refundId,
                WxRefundId = "wx-" + refundId,
                RefundFee = fen,
                RetCode = 0,
                payer.OpenId
            }), "", Token);
            await f.Service.ReconcileAsync(Token);
        }
    }

    [Theory, InlineData(true), InlineData(false), Trait("Category", "Integration")]
    public async Task RecordedFullRefundOfExpiredRegistrationRequiresMatchingPaymentEvidence(bool matchingPayment)
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        var refundId = Guid.NewGuid().ToString("N");
        await using (var db = new SnowShotDbContext(f.Options))
        {
            await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).ExecuteUpdateAsync(s =>
                s.SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
            var retained = await db.AccountPaymentOrders.SingleAsync(x => x.Id == order.OrderId, Token);
            retained.State = "reconciliation_required";
            retained.PaidFen = 990;
            retained.PlatformTransactionId = "wx-" + order.OrderId;
            db.AccountRefunds.Add(new()
            {
                Id = refundId,
                OrderId = order.OrderId,
                RefundFen = 990,
                State = "completed",
                PlatformRefundId = "wx-" + refundId,
                SuccessNotified = true,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            db.AccountFinancialEntries.Add(new()
            {
                Id = "refund:" + refundId,
                AccountId = retained.AccountId,
                OrderId = order.OrderId,
                Type = "refund",
                Status = "completed",
                AmountNanoYuan = -9_900_000_000L,
                Description = "Platform refund",
                OccurredAt = DateTimeOffset.UtcNow
            });
            var notice = JsonSerializer.Serialize(new
            {
                Event = "xpay_refund_notify",
                MchOrderId = order.OrderId,
                WxOrderId = matchingPayment ? retained.PlatformTransactionId : "different-payment",
                MchRefundId = refundId,
                WxRefundId = "wx-" + refundId,
                RefundFee = 990,
                RetCode = 0,
                payer.OpenId
            });
            db.AccountPlatformEvents.Add(new()
            {
                Id = Convert.ToHexStringLower(AccountSecrets.Hash(notice)),
                EventName = "xpay_refund_notify",
                PayloadEncrypted = f.Secrets.Protect(notice),
                State = "processed",
                ReceivedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(Token);
        }
        Assert.Equal("expired", (await f.Service.PublicRequestAsync(registration.RequestId, payer, Token)).State);
        f.Platform.FailQueries = matchingPayment;
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        Assert.Equal(matchingPayment ? "refunded" : "reconciliation_required",
            (await f.Service.RefreshOrderAsync(payer, order.OrderId, Token)).State);
        Assert.Equal(matchingPayment, (await f.Service.DeletionEligibilityAsync(payer, Token)).CanDelete);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ExpiryKeepsAnActivatedUsernameWhenPaymentRequiresReconciliation()
    {
        var f = Fixture(); var username = Name(); var registration = await Register(f, username); var payer = await Mini(f);
        var order = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, payer.OpenId!, "wx-" + order.OrderId), "", Token);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).ExecuteUpdateAsync(x => x.SetProperty(y => y.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)), Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, payer.OpenId!, 990, "paid", "conflict-" + order.OrderId, [], 990);
        var status = await f.Service.RefreshOrderAsync(payer, order.OrderId, Token);
        Assert.Equal("reconciliation_required", status.State);

        var conflict = await Assert.ThrowsAsync<AccountException>(() => Register(f, username));
        Assert.Equal("username_unavailable", conflict.Code);
        var session = await f.Service.LoginAsync(username, "password-123", Guid.NewGuid().ToString(), Token);
        Assert.NotNull(session.AccountId);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentRegistrationsCannotReserveTheSameWeChatIdentity()
    {
        var f = Fixture(); var a = await Register(f, Name()); var b = await Register(f, Name()); var mini = await Mini(f);
        var results = await Task.WhenAll(new[] { a, b }.Select(async request =>
        {
            try { await f.Service.CreateOrderAsync(mini, request.RequestId, "cny-9-9", Token); return "issued"; }
            catch (AccountException error) { return error.Code; }
        }));
        Assert.Single(results, x => x == "issued"); Assert.Single(results, x => x == "wechat_link_conflict");
    }

    [Fact, Trait("Category", "Integration")]
    public async Task VerifiedPaymentCreditsOnceAndClaimRecoversWithoutPasswordPersistence()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var inactive = await Assert.ThrowsAsync<AccountException>(() => f.Service.LoginAsync(registration.Username!, "password-123", Guid.NewGuid().ToString(), Token));
        Assert.Equal("account_inactive", inactive.Code);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => f.Service.RefreshOrderAsync(mini, order.OrderId, Token)));
        var first = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var retry = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        Assert.Equal(first.AccessToken, retry.AccessToken);
        var identity = await f.Service.ResolveAsync(first.AccessToken, Token); Assert.NotNull(identity);
        var account = await f.Service.SummaryAsync(identity, Token); Assert.Equal("9900000000", account.AvailableCreditNanoYuan);
        Assert.Equal("9900000000", account.CumulativeRechargeNanoYuan);
        Assert.Equal("cny-9-9", account.MembershipTier);
        await using (var db = new SnowShotDbContext(f.Options))
        {
            Assert.Single(await db.AccountFinancialEntries.Where(x => x.AccountId == identity.AccountId).ToListAsync(Token));
            Assert.Single(await db.AccountWechatLinks.Where(x => x.AccountId == identity.AccountId).ToListAsync(Token));
            await db.AccountSessions.Where(x => x.Id == identity.SessionId).ExecuteUpdateAsync(x => x.SetProperty(y => y.AccessExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        }
        var renewed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        Assert.NotEqual(first.AccessToken, renewed.AccessToken); Assert.NotNull(await f.Service.ResolveAsync(renewed.AccessToken, Token));
        await f.Service.LogoutAsync(renewed.RefreshToken, Token);
        await Assert.ThrowsAsync<AccountException>(() => f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LatePaymentNeverActivatesAReusedUsername()
    {
        var f = Fixture(); var username = Name(); var first = await Register(f, username); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, first.RequestId, "cny-9-9", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 0, "closed", null, []);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var replacement = await Register(f, username);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        await ReleaseQueryCooldown(f, order.OrderId);
        var status = await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        Assert.Equal("reconciliation_required", status.State);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.False(await db.Accounts.Where(x => x.Id == db.AccountPurchaseRequests.Where(r => r.Id == replacement.RequestId).Select(r => r.AccountId).Single()).Select(x => x.Active).SingleAsync(Token));
        Assert.Equal(0, await db.AccountWallets.Where(x => x.AccountId == db.AccountPurchaseRequests.Where(r => r.Id == first.RequestId).Select(r => r.AccountId).Single()).Select(x => x.AvailableNanoYuan).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RefundBeforeFulfillmentIsAtomicAndDuplicateNotificationsDoNotDeductAgain()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.PaymentsAvailable = false;
        var refundId = Guid.NewGuid().ToString("N");
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        f.Platform.Orders[refundId] = new(refundId, mini.OpenId!, 0, "refund", "wx-" + refundId, [new(refundId, 990, "completed")]);
        var payload = JsonSerializer.Serialize(new
        {
            Event = "xpay_refund_notify",
            MchOrderId = order.OrderId,
            WxOrderId = "wx-" + order.OrderId,
            MchRefundId = refundId,
            WxRefundId = "wx-" + refundId,
            RefundFee = 990,
            RetCode = 0,
            OpenId = mini.OpenId
        });
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        await f.Service.ReconcileAsync(Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        await f.Service.ReconcileAsync(Token);
        var claimed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var identity = (await f.Service.ResolveAsync(claimed.AccessToken, Token))!;
        var account = await f.Service.SummaryAsync(identity, Token);
        Assert.Equal("active", account.MembershipStatus); Assert.Equal("0", account.AvailableCreditNanoYuan); Assert.Equal("0", account.OutstandingBalanceNanoYuan);
        Assert.Equal("0", account.CumulativeRechargeNanoYuan);
        Assert.Null(account.MembershipTier);
        Assert.False(account.FreeLatexExtraction);
        var transactions = await f.Service.TransactionsAsync(identity, null, 20, Token);
        Assert.Equal(2, transactions.Items.Count); Assert.All(transactions.Items, x => Assert.Equal("completed", x.Status));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DisablingNewPurchasesPreservesPaymentAndDeliveryReconciliation()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.PaymentsAvailable = false; f.Platform.FailDelivery = true;
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        var paid = await f.Service.RefreshOrderAsync(mini, order.OrderId, Token); Assert.Equal("paid", paid.State);
        await using (var db = new SnowShotDbContext(f.Options)) Assert.False(await db.AccountPaymentOrders.Where(x => x.Id == order.OrderId).Select(x => x.DeliveryConfirmed).SingleAsync(Token));
        f.Platform.FailDelivery = false;
        await using (var db = new SnowShotDbContext(f.Options))
        {
            var retry = await db.AccountPaymentOrders.SingleAsync(x => x.Id == order.OrderId, Token);
            Assert.NotNull(retry.NextDeliveryAttemptAt);
            Assert.Null(retry.DeliveryLeaseToken);
            // Advance only this record's durable schedule, without waiting on wall time.
            await db.AccountPaymentOrders.Where(x => x.Id == order.OrderId).ExecuteUpdateAsync(
                set => set.SetProperty(x => x.NextDeliveryAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Token);
        }
        await f.Service.ReconcileAsync(Token);
        await using (var db = new SnowShotDbContext(f.Options)) Assert.True(await db.AccountPaymentOrders.Where(x => x.Id == order.OrderId).Select(x => x.DeliveryConfirmed).SingleAsync(Token));
        var claimed = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var account = await f.Service.SummaryAsync((await f.Service.ResolveAsync(claimed.AccessToken, Token))!, Token);
        Assert.Equal("9900000000", account.AvailableCreditNanoYuan); Assert.False(account.PaymentsAvailable);
        var error = await Assert.ThrowsAsync<AccountException>(() => Register(f, Name())); Assert.Equal("payments_unavailable", error.Code);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CompletedRefundCreatesDebtAndSubsequentTopUpRepaysIt()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-9-9", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 990, "paid", "wx-" + order.OrderId, [], 990);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var tokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        await using (var db = new SnowShotDbContext(f.Options)) await db.AccountWallets.Where(x => x.AccountId == desktop.AccountId)
            .ExecuteUpdateAsync(x => x.SetProperty(y => y.AvailableNanoYuan, 0), Token);
        var refundId = Guid.NewGuid().ToString("N");
        f.Platform.Orders[refundId] = new(refundId, mini.OpenId!, 0, "refund", "wx-" + refundId, [new(refundId, 990, "completed")]);
        var payload = JsonSerializer.Serialize(new
        {
            Event = "xpay_refund_notify",
            MchOrderId = order.OrderId,
            WxOrderId = "wx-" + order.OrderId,
            MchRefundId = refundId,
            WxRefundId = "wx-" + refundId,
            RefundFee = 990,
            RetCode = 0,
            OpenId = mini.OpenId
        });
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token); await f.Service.ReconcileAsync(Token);
        Assert.Equal("9900000000", (await f.Service.SummaryAsync(desktop, Token)).OutstandingBalanceNanoYuan);
        var topup = await f.Service.TopUpAsync(desktop, Token);
        mini = mini with { AccountId = desktop.AccountId };
        var additional = await f.Service.CreateOrderAsync(mini, topup.RequestId, "cny-29-9", Token);
        f.Platform.Orders[additional.OrderId] = new(additional.OrderId, mini.OpenId!, 2990, "paid", "wx-" + additional.OrderId, [], 2990);
        await f.Service.RefreshOrderAsync(mini, additional.OrderId, Token);
        var after = await f.Service.SummaryAsync(desktop, Token);
        Assert.Equal("0", after.OutstandingBalanceNanoYuan); Assert.Equal("20000000000", after.AvailableCreditNanoYuan);
        Assert.Equal("29900000000", after.CumulativeRechargeNanoYuan);
        Assert.Equal("cny-29-9", after.MembershipTier);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MultipleVerifiedPaymentsUpgradeMembershipUsingActualPaymentsEvenAfterCreditsAreSpent()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var first = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-88-8", Token);
        f.Platform.Orders[first.OrderId] = new(first.OrderId, mini.OpenId!, 8880, "paid", "wx-" + first.OrderId, [], 8880);
        await f.Service.RefreshOrderAsync(mini, first.OrderId, Token);
        var tokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        mini = mini with { AccountId = desktop.AccountId };
        Assert.Equal("cny-88-8", (await f.Service.SummaryAsync(desktop, Token)).MembershipTier);
        foreach (var (package, paid, price, total, tier) in new[]
        {
            ("cny-188", 9920, 18800, "188000000000", "cny-188"),
            ("cny-999", 81100, 99900, "999000000000", "cny-999"),
        })
        {
            var request = await f.Service.TopUpAsync(desktop, Token);
            var order = await f.Service.CreateOrderAsync(mini, request.RequestId, package, Token);
            var pending = await f.Service.SummaryAsync(desktop, Token);
            Assert.NotEqual(total, pending.CumulativeRechargeNanoYuan);
            f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, paid, "paid", "wx-" + order.OrderId, [], price);
            await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Service.RefreshOrderAsync(mini, order.OrderId, Token)));
            await using (var db = new SnowShotDbContext(f.Options))
                await db.AccountWallets.Where(x => x.AccountId == desktop.AccountId).ExecuteUpdateAsync(set => set.SetProperty(x => x.AvailableNanoYuan, 0L), Token);
            var summary = await f.Service.SummaryAsync(desktop, Token);
            Assert.Equal(total, summary.CumulativeRechargeNanoYuan);
            Assert.Equal(tier, summary.MembershipTier);
            Assert.True(summary.FreeTableExtraction);
            Assert.True(summary.FreeLatexExtraction);
            Assert.Equal("0", summary.AvailableCreditNanoYuan);
        }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task OnlyCompletedPartialRefundDowngradesMembershipAndRepeatsDoNotDeductAgain()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var mini = await Mini(f);
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-188", Token);
        f.Platform.Orders[order.OrderId] = new(order.OrderId, mini.OpenId!, 18800, "paid", "wx-" + order.OrderId, [], 18800);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        var tokens = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var desktop = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountWallets.Where(x => x.AccountId == desktop.AccountId).ExecuteUpdateAsync(set => set.SetProperty(x => x.AvailableNanoYuan, 0L), Token);
        var refundId = Guid.NewGuid().ToString("N");
        f.Platform.Orders[refundId] = new(refundId, mini.OpenId!, 0, "refund", "wx-" + refundId, [new(refundId, 1, "processing")]);
        var payload = JsonSerializer.Serialize(new
        {
            Event = "xpay_refund_notify",
            MchOrderId = order.OrderId,
            WxOrderId = "wx-" + order.OrderId,
            MchRefundId = refundId,
            WxRefundId = "wx-" + refundId,
            RefundFee = 1,
            RetCode = 0,
            OpenId = mini.OpenId,
        });
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        var processing = await f.Service.SummaryAsync(desktop, Token);
        Assert.Equal("188000000000", processing.CumulativeRechargeNanoYuan);
        Assert.True(processing.FreeTableExtraction);
        f.Platform.Orders[refundId] = f.Platform.Orders[refundId] with { Refunds = [new(refundId, 1, "completed")] };
        await f.Service.VerifyCallbackAsync("", "", "", null, " " + payload, "", Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, "  " + payload, "", Token);
        await f.Service.ReconcileAsync(Token);
        var refunded = await f.Service.SummaryAsync(desktop, Token);
        Assert.Equal("187990000000", refunded.CumulativeRechargeNanoYuan);
        Assert.Equal("cny-88-8", refunded.MembershipTier);
        Assert.False(refunded.FreeTableExtraction);
        Assert.False(refunded.FreeLatexExtraction);
        Assert.Equal("10000000", refunded.OutstandingBalanceNanoYuan);
        Assert.Equal("active", refunded.MembershipStatus);
    }

    private static async Task<AccountPurchaseRequest> Register(TestFixture f, string username, Action? beforeSubmit = null)
    {
        var id = Guid.NewGuid(); var client = Guid.NewGuid().ToString("N");
        await using (var db = new SnowShotDbContext(f.Options))
        {
            db.AccountCaptchas.Add(new() { Id = id, ClientKey = f.Secrets.Fingerprint(client), AnswerHash = AccountSecrets.Hash(id.ToString("N") + ":ABC234"), ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) });
            await db.SaveChangesAsync(Token);
        }
        beforeSubmit?.Invoke();
        return await f.Service.RegisterAsync(new(username, "password-123", "password-123", id, "ABC234"), client, Token);
    }
    private static async Task<AccountSessionIdentity> Mini(TestFixture f)
    {
        var tokens = await f.Service.WechatLoginAsync("openid-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Token);
        return (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
    }
    private static async Task ReleaseQueryCooldown(TestFixture f, string orderId)
    {
        await using var db = new SnowShotDbContext(f.Options);
        await db.AccountPaymentOrders.Where(x => x.Id == orderId).ExecuteUpdateAsync(x => x.SetProperty(y => y.LastQueriedAt, (DateTimeOffset?)null), Token);
    }
    private static TestFixture Fixture(LostCommitAcknowledgement? fault = null, bool allowPaymentTesting = false)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SNOWSHOT_RUN_INTEGRATION"), "true", StringComparison.OrdinalIgnoreCase)) Assert.Skip("Enable the isolated PostgreSQL integration fixture.");
        var connection = Environment.GetEnvironmentVariable("ConnectionStrings__SnowShot") ?? throw new InvalidOperationException("A test PostgreSQL connection is required.");
        var options = fault is null ? DistributedIntegrationTests.DatabaseOptions(connection) : new DbContextOptionsBuilder<SnowShotDbContext>()
            .UseNpgsql(connection, postgres => postgres.MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema).EnableRetryOnFailure(3))
            .AddInterceptors(fault).Options;
        var platform = new FakePlatform();
        var secrets = new AccountSecrets(new IdentityOptions { HmacKeyBase64 = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) });
        return new(options, platform, secrets, AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(options), platform, secrets, TimeProvider.System,
            new() { AllowPaymentTesting = allowPaymentTesting }));
    }
    private static string Name() => "acct_" + Guid.NewGuid().ToString("N")[..20];
    private sealed record TestFixture(DbContextOptions<SnowShotDbContext> Options, FakePlatform Platform, AccountSecrets Secrets, PostgresAccountService Service);
    private sealed class FakePlatform : IWechatAccountPlatform
    {
        public bool PaymentsAvailable { get; set; } = true;
        public bool ReconciliationAvailable => true;
        public bool FailQueries { get; set; }
        public bool FailDelivery { get; set; }
        public HashSet<string> UnavailableDeliveries { get; } = new(StringComparer.Ordinal);
        public ConcurrentBag<string> DeliveryCalls { get; } = [];
        public Action? CodeGenerated { get; set; }
        public Action? OrderQueried { get; set; }
        public ConcurrentDictionary<string, VerifiedVirtualOrder> Orders { get; } = new(StringComparer.Ordinal);
        public Task<WechatLoginIdentity> LoginAsync(string code, CancellationToken token) => Task.FromResult(new WechatLoginIdentity(code, "session-key"));
        public Task<byte[]> CodeAsync(Guid id, CancellationToken token)
        {
            var png = CaptchaImage.Create().Png; CodeGenerated?.Invoke(); return Task.FromResult(png);
        }
        public VirtualPaymentParameters Payment(string id, string openId, string sessionKey, string package, int price)
        {
            var signData = JsonSerializer.Serialize(new { env = 0, currencyType = "CNY", outTradeNo = id, attach = id, productId = package, buyQuantity = 1, goodsPrice = price });
            return new("short_series_goods", signData, "pay-sig", WechatAccountPlatform.Sign(sessionKey, signData));
        }
        public VirtualPaymentParameters RefreshPaymentAuthorization(VirtualPaymentParameters parameters, string sessionKey) =>
            parameters with { Signature = WechatAccountPlatform.Sign(sessionKey, parameters.SignData) };
        public Task<VerifiedVirtualOrder?> QueryAsync(string id, string openId, CancellationToken token)
        {
            if (FailQueries) throw new HttpRequestException("Synthetic network error.");
            OrderQueried?.Invoke();
            return Task.FromResult(Orders.GetValueOrDefault(id));
        }
        public Task NotifyDeliveryAsync(string id, CancellationToken token)
        {
            DeliveryCalls.Add(id);
            return FailDelivery || UnavailableDeliveries.Contains(id)
                ? throw new HttpRequestException("Synthetic delivery confirmation failure.") : Task.CompletedTask;
        }
        public string VerifyMessage(string signature, string timestamp, string nonce, string? echo, string? body, string? messageSignature) => body ?? echo ?? "";
    }
    private sealed class LostCommitAcknowledgement : DbTransactionInterceptor
    {
        private int _armed;
        public void Arm() => Interlocked.Exchange(ref _armed, 1);
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _armed, 0) == 1) throw new NpgsqlException("Synthetic lost commit acknowledgement.", new TimeoutException());
            return Task.CompletedTask;
        }
    }
}
