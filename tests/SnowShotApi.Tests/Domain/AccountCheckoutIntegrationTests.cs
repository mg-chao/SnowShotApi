using Microsoft.EntityFrameworkCore;
using SnowShot.Application;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task CheckoutWaitingForIdentityLockCannotIssueAfterRegistrationExpires()
    {
        var f = Fixture();
        var payer = await Mini(f);
        var registration = await Register(f, Name());
        await using var db = new SnowShotDbContext(f.Options);
        var connection = new Npgsql.NpgsqlConnectionStringBuilder(db.Database.GetConnectionString())
        {
            ApplicationName = Guid.NewGuid().ToString("N")
        };
        var service = AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(
            DistributedIntegrationTests.DatabaseOptions(connection.ConnectionString)), f.Platform, f.Secrets, TimeProvider.System, new());

        await using var gate = new SnowShotDbContext(f.Options);
        await using var transaction = await gate.Database.BeginTransactionAsync(Token);
        await AccountSynchronization.ChangeIdentityAsync(gate, Token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(4);
        await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAt, deadline), Token);
        var checkout = service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, Guid.NewGuid());
        bool waiting;
        do
        {
            waiting = await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (SELECT FROM pg_stat_activity WHERE application_name={connection.ApplicationName}
                    AND wait_event_type='Lock' AND wait_event='advisory') AS "Value"
                """).SingleAsync(Token);
            if (!waiting) await Task.Delay(30, Token);
        } while (!waiting && DateTimeOffset.UtcNow < deadline);
        Assert.True(waiting && DateTimeOffset.UtcNow < deadline);
        await Task.Delay(deadline - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100), Token);
        await transaction.CommitAsync(Token);

        var error = await Assert.ThrowsAsync<AccountException>(() => checkout.WaitAsync(TimeSpan.FromSeconds(10), Token));
        Assert.Equal("request_expired", error.Code);
        Assert.Equal("expired", await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId)
            .Select(x => x.State).SingleAsync(Token));
        Assert.False(await db.AccountPaymentOrders.AnyAsync(x => x.RequestId == registration.RequestId, Token));
        Assert.False(await db.AccountUsernameClaims.AnyAsync(x => x.RequestId == registration.RequestId, Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LostCheckoutHttpResponseRecoversTheSameAttemptAfterServiceRestart()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        // The client retained its attempt ID before sending HTTP, but did not
        // receive this response or invoke the SDK. Recovery cannot rely on RAM.
        var restarted = AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(f.Options),
            f.Platform, f.Secrets, TimeProvider.System, new());
        var recovered = await restarted.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);

        Assert.NotNull(issued.PaymentParameters);
        Assert.Equal(issued.OrderId, recovered.OrderId);
        Assert.Equal(issued.PaymentParameters, recovered.PaymentParameters);
        await using var db = new SnowShotDbContext(f.Options);
        var stored = Assert.Single(await db.AccountPaymentOrders.Where(x => x.RequestId == registration.RequestId).ToListAsync(Token));
        Assert.Equal(attempt, stored.CheckoutAttemptId);
        Assert.Single(await db.AccountFinancialEntries.Where(x => x.OrderId == issued.OrderId).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CheckoutWithWithdrawnPackageCanReselectOnlyBeforeAnOrderIsIssued()
    {
        var f = Fixture(allowPaymentTesting: true);
        var payer = await Mini(f);
        var unissued = await Register(f, Name());
        var issued = await Register(f, Name());
        var issuedPayer = await Mini(f);
        var issuedAttempt = Guid.NewGuid();
        var originalOrder = await f.Service.CreateOrderAsync(issuedPayer, issued.RequestId, "cny-0-1", Token, issuedAttempt);
        var restarted = AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(f.Options),
            f.Platform, f.Secrets, TimeProvider.System, new());

        var unissuedAttempt = Guid.NewGuid();
        foreach (var package in new[] { "cny-0-1", "" })
        {
            var error = await Assert.ThrowsAsync<AccountException>(() =>
                restarted.CreateOrderAsync(payer, unissued.RequestId, package, Token, unissuedAttempt));
            Assert.Equal("checkout_not_issued", error.Code);

            var recovery = await restarted.CreateOrderAsync(issuedPayer, issued.RequestId, package, Token, issuedAttempt);
            Assert.Equal(originalOrder.OrderId, recovery.OrderId);
            Assert.Null(recovery.PaymentParameters);
        }
        var legacyError = await Assert.ThrowsAsync<AccountException>(() =>
            restarted.CreateOrderAsync(payer, unissued.RequestId, "cny-0-1", Token));
        Assert.Equal("package_unavailable", legacyError.Code);

        await using var db = new SnowShotDbContext(f.Options);
        Assert.False(await db.AccountPaymentOrders.AnyAsync(x => x.RequestId == unissued.RequestId, Token));
        var replacement = await restarted.CreateOrderAsync(payer, unissued.RequestId, "cny-9-9", Token, Guid.NewGuid());
        Assert.NotNull(replacement.PaymentParameters);
        Assert.Single(await db.AccountPaymentOrders.Where(x => x.RequestId == issued.RequestId).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LostCheckoutCommitAcknowledgementReturnsLegacyParametersFromTheSameInvocation()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault);
        var registration = await Register(f, Name()); var payer = await Mini(f);
        fault.Arm();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);

        Assert.NotNull(issued.PaymentParameters);
        var separateInvocation = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token);
        Assert.Equal(issued.OrderId, separateInvocation.OrderId);
        Assert.Null(separateInvocation.PaymentParameters);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountPaymentOrders.Where(x => x.RequestId == registration.RequestId).ToListAsync(Token));
        Assert.Single(await db.AccountFinancialEntries.Where(x => x.OrderId == issued.OrderId).ToListAsync(Token));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnotherAttemptOrLegacyCallerCannotRecoverCheckoutParameters(bool legacy)
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        var other = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token,
            legacy ? null : Guid.NewGuid());

        Assert.Equal(issued.OrderId, other.OrderId);
        Assert.Null(other.PaymentParameters);
        var recovered = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        Assert.NotNull(recovered.PaymentParameters);
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutAttemptCannotBeReusedForAnotherRequestOrPackage(bool anotherRequest)
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        var target = anotherRequest ? await Register(f, Name()) : registration;
        var error = await Assert.ThrowsAsync<AccountException>(() => f.Service.CreateOrderAsync(payer,
            target.RequestId, anotherRequest ? "cny-9-9" : "cny-29-9", Token, attempt));

        Assert.Equal("checkout_attempt_conflict", error.Code);
        Assert.Equal(409, error.Status);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Equal(issued.OrderId, await db.AccountPaymentOrders.Where(x => x.CheckoutAttemptId == attempt).Select(x => x.Id).SingleAsync(Token));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CheckoutAttemptNeverExposesAnotherPayersOrder(bool anotherRequest)
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        var other = await Mini(f);
        var target = anotherRequest ? await Register(f, Name()) : registration;
        var error = await Assert.ThrowsAsync<AccountException>(() => f.Service.CreateOrderAsync(other,
            target.RequestId, "cny-9-9", Token, attempt));

        Assert.Equal("wechat_link_conflict", error.Code);
        Assert.Equal(409, error.Status);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountPaymentOrders.Where(x => x.CheckoutAttemptId == attempt).ToListAsync(Token));
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData("paid", true)]
    [InlineData("closed", false)]
    [InlineData("failed", false)]
    [InlineData("reconciliation_required", false)]
    [InlineData("payment_pending", true)]
    public async Task SettledOrFulfilledOrdersNeverReplayCheckoutParameters(string state, bool fulfilled)
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPaymentOrders.Where(x => x.Id == issued.OrderId).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.State, state).SetProperty(x => x.Fulfilled, fulfilled), Token);

        var repeated = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        Assert.Equal(issued.OrderId, repeated.OrderId);
        Assert.Equal(state, repeated.State);
        Assert.Null(repeated.PaymentParameters);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentRetriesOfOneCheckoutAttemptCreateOneRecoverableOrder()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var orders = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt)));

        Assert.Single(orders.Select(x => x.OrderId).Distinct());
        Assert.All(orders, order => Assert.Equal(orders[0].PaymentParameters, order.PaymentParameters));
        Assert.NotNull(orders[0].PaymentParameters);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountPaymentOrders.Where(x => x.RequestId == registration.RequestId).ToListAsync(Token));
        Assert.Single(await db.AccountFinancialEntries.Where(x => x.OrderId == orders[0].OrderId).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task ConcurrentDistinctCheckoutAttemptsExposeOnlyTheWinningOrderParameters()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var orders = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, Guid.NewGuid())));

        Assert.Single(orders.Select(x => x.OrderId).Distinct());
        Assert.Single(orders, order => order.PaymentParameters is not null);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Single(await db.AccountPaymentOrders.Where(x => x.RequestId == registration.RequestId).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CheckoutRecoveryReauthorizesTheOriginalPaymentForTheCurrentWechatSession()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        var refreshed = await f.Service.WechatLoginAsync(payer.OpenId!, Guid.NewGuid().ToString("N"), Token);
        var current = (await f.Service.ResolveAsync(refreshed.AccessToken, Token))!;
        const string currentKey = "rotated-session-key";
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountSessions.Where(x => x.Id == current.SessionId).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.WechatSessionKeyEncrypted, f.Secrets.Protect(currentKey)), Token);

        var recovered = await f.Service.CreateOrderAsync(current, registration.RequestId, "cny-9-9", Token, attempt);
        var original = Assert.IsType<VirtualPaymentParameters>(issued.PaymentParameters);
        var authorized = Assert.IsType<VirtualPaymentParameters>(recovered.PaymentParameters);
        Assert.Equal(issued.OrderId, recovered.OrderId);
        Assert.Equal(original.SignData, authorized.SignData);
        Assert.Equal(original.PaySig, authorized.PaySig);
        Assert.NotEqual(original.Signature, authorized.Signature);
        Assert.Equal(WechatAccountPlatform.Sign(currentKey, original.SignData), authorized.Signature);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MissingPlatformOrderRemainsPendingAndRecoverableWithoutReleasingIdentityClaims()
    {
        var f = Fixture(); var payer = await Mini(f);
        var request = await f.Service.MiniProgramPurchaseAsync(payer, Token);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, request.RequestId, "cny-9-9", Token, attempt);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPurchaseRequests.Where(x => x.Id == request.RequestId).ExecuteUpdateAsync(set => set
                .SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)), Token);

        var queried = await f.Service.RefreshOrderAsync(payer, issued.OrderId, Token);
        Assert.Equal("payment_pending", queried.State);
        Assert.Equal(request.RequestId, (await f.Service.MiniProgramPurchaseAsync(payer, Token)).RequestId);
        Assert.NotNull((await f.Service.CreateOrderAsync(payer, request.RequestId, "cny-9-9", Token, attempt)).PaymentParameters);
        Assert.Contains("pending_payment", (await f.Service.DeletionEligibilityAsync(payer, Token)).Blockers);
        await using var read = new SnowShotDbContext(f.Options);
        Assert.Single(await read.AccountPayerClaims.Where(x => x.RequestId == request.RequestId).ToListAsync(Token));
        Assert.Single(await read.AccountUsernameClaims.Where(x => x.RequestId == request.RequestId).ToListAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task PlatformObservedPendingOrderCannotBeSubmittedAgainThroughAttemptRecovery()
    {
        var f = Fixture(); var registration = await Register(f, Name()); var payer = await Mini(f);
        var attempt = Guid.NewGuid();
        var issued = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        f.Platform.Orders[issued.OrderId] = new(issued.OrderId, payer.OpenId!, 0, "payment_pending", null, [], 990);

        Assert.Equal("payment_pending", (await f.Service.RefreshOrderAsync(payer, issued.OrderId, Token)).State);
        var repeated = await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        Assert.Equal(issued.OrderId, repeated.OrderId);
        Assert.Null(repeated.PaymentParameters);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.NotNull(await db.AccountPaymentOrders.Where(x => x.Id == issued.OrderId).Select(x => x.VerifiedAt).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task RevokedWechatSessionCannotReplayItsCheckoutAttempt()
    {
        var f = Fixture(); var registration = await Register(f, Name());
        var tokens = await f.Service.WechatLoginAsync("openid-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Token);
        var payer = (await f.Service.ResolveAsync(tokens.AccessToken, Token))!;
        var attempt = Guid.NewGuid();
        await f.Service.CreateOrderAsync(payer, registration.RequestId, "cny-9-9", Token, attempt);
        await f.Service.LogoutAsync(tokens.RefreshToken, Token);

        var error = await Assert.ThrowsAsync<AccountException>(() => f.Service.CreateOrderAsync(payer,
            registration.RequestId, "cny-9-9", Token, attempt));
        Assert.Equal("invalid_session", error.Code);
        Assert.Equal(401, error.Status);
    }
}
