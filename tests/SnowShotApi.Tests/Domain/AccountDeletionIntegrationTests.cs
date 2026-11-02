using System.Security.Cryptography;
using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SnowShot.Application;
using SnowShot.Domain;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task DeletionAnonymizesCredentialsRevokesEverySessionAndNeverRestoresPurchasedRights()
    {
        var f = Fixture();
        var registration = await Register(f, Name());
        var original = await f.Service.WechatLoginAsync("openid-" + Guid.NewGuid().ToString("N"), Name(), Token);
        var mini = (await f.Service.ResolveAsync(original.AccessToken, Token))!;
        var order = await f.Service.CreateOrderAsync(mini, registration.RequestId, "cny-188", Token);
        await PayDirect(f, mini, order, 18800);
        var desktop = await f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token);
        var another = await f.Service.WechatLoginAsync(mini.OpenId!, Name(), Token);
        var state = await f.Service.DeletionEligibilityAsync(mini, Token);
        Assert.True(state.CanDelete);
        Assert.Equal("188000000000", state.AvailableCreditNanoYuan);
        Assert.Equal("cny-188", state.MembershipTier);
        Assert.True((await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, state.DeletionVersion), Token)).Deleted);

        Assert.Null(await f.Service.ResolveAsync(original.AccessToken, Token));
        Assert.Null(await f.Service.ResolveAsync(desktop.AccessToken, Token));
        Assert.Null(await f.Service.ResolveAsync(another.AccessToken, Token));
        Assert.Equal("invalid_session", (await Assert.ThrowsAsync<AccountException>(() => f.Service.RefreshSessionAsync(desktop.RefreshToken, Token))).Code);
        Assert.Equal("invalid_session", (await Assert.ThrowsAsync<AccountException>(() => f.Service.RefreshSessionAsync(original.RefreshToken, Token))).Code);
        Assert.Equal("invalid_credentials", (await Assert.ThrowsAsync<AccountException>(() => f.Service.LoginAsync(registration.Username!, "password-123", Name(), Token))).Code);
        Assert.Equal("invalid_registration_proof", (await Assert.ThrowsAsync<AccountException>(() => f.Service.ClaimAsync(registration.RequestId, registration.RegistrationProof!, Token))).Code);
        await using var db = new SnowShotDbContext(f.Options);
        var account = await db.Accounts.SingleAsync(x => x.Id == desktop.AccountId, Token);
        Assert.False(account.Active);
        Assert.NotNull(account.DeletedAt);
        Assert.Empty(account.PasswordHash);
        Assert.NotEqual(registration.Username, account.Username);
        Assert.Equal("account-deletion-v1", account.DeletionConsentVersion);
        Assert.Equal(188000000000L, account.ForfeitedCreditNanoYuan);
        Assert.False(await db.AccountUsernameClaims.AnyAsync(x => x.AccountId == account.Id, Token));
        Assert.False(await db.AccountWechatLinks.AnyAsync(x => x.AccountId == account.Id, Token));
        Assert.False(await db.AccountSessions.AnyAsync(x => x.AccountId == account.Id || x.OpenId == mini.OpenId, Token));
        Assert.Equal(2, await db.AccountFinancialEntries.CountAsync(x => x.AccountId == account.Id, Token));
        var wallet = await db.AccountWallets.SingleAsync(x => x.AccountId == account.Id, Token);
        Assert.Equal(0, wallet.AvailableNanoYuan);
        Assert.Equal(0, wallet.CumulativeRechargeNanoYuan);
        var request = await db.AccountPurchaseRequests.SingleAsync(x => x.Id == registration.RequestId, Token);
        Assert.Null(request.ProofHash); Assert.Null(request.BootstrapTokensEncrypted); Assert.Null(request.CodeImage);
        Assert.Empty((await db.AccountPaymentOrders.SingleAsync(x => x.Id == order.OrderId, Token)).PaymentParametersEncrypted);

        // Delayed historical payment refresh/callbacks must never reactivate it.
        await ReleaseQueryCooldown(f, order.OrderId);
        await f.Service.RefreshOrderAsync(mini, order.OrderId, Token);
        await f.Service.VerifyCallbackAsync("", "", "", null, DeliveryPayload(order.OrderId, mini.OpenId!, "wx-" + order.OrderId), "", Token);
        await db.Entry(account).ReloadAsync(Token);
        Assert.False(account.Active);
        var renewed = await f.Service.WechatLoginAsync(mini.OpenId!, Name(), Token);
        var fresh = (await f.Service.ResolveAsync(renewed.AccessToken, Token))!;
        Assert.Null(fresh.AccountId);
        Assert.False((await f.Service.SummaryAsync(fresh, Token)).FreeTableExtraction);
        var purchase = await f.Service.MiniProgramPurchaseAsync(fresh, Token);
        Assert.Equal("mini_registration", purchase.Purpose);
        Assert.NotEqual(account.Id, await db.AccountPurchaseRequests.Where(x => x.Id == purchase.RequestId).Select(x => x.AccountId).SingleAsync(Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DeletionRequiresMatchingWechatExplicitForfeitureAndAnUnchangedBalanceSnapshot()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        await PayDirect(f, mini, await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token), 990);
        var state = await f.Service.DeletionEligibilityAsync(mini, Token);
        Assert.Equal("cny-9-9", state.MembershipTier);
        Assert.Equal("invalid_confirmation", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, false, state.DeletionVersion), Token))).Code);
        Assert.Equal("wechat_identity_mismatch", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.DeleteAsync(mini, new("DELETE", "another-openid", true, state.DeletionVersion), Token))).Code);
        await using var db = new SnowShotDbContext(f.Options);
        var accountId = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        await db.AccountWallets.Where(x => x.AccountId == accountId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.AvailableNanoYuan, 123L).SetProperty(x => x.CumulativeRechargeNanoYuan, 0L), Token);
        Assert.Null((await f.Service.DeletionEligibilityAsync(mini, Token)).MembershipTier);
        Assert.Equal("deletion_state_changed", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, state.DeletionVersion), Token))).Code);
        Assert.True(await db.Accounts.AnyAsync(x => x.Id == accountId && x.Active, Token));
        Assert.Equal("invalid_session", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.DeletionEligibilityAsync(mini with { SessionId = Guid.NewGuid() }, Token))).Code);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DeletionBlocksUnsettledRefundsDebtAndHeldUsageButAllowsCompletedHistory()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var order = await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token);
        Assert.Contains("pending_payment", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
        await PayDirect(f, mini, order, 990);
        await using var db = new SnowShotDbContext(f.Options);
        var accountId = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        await db.AccountWallets.Where(x => x.AccountId == accountId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.OutstandingNanoYuan, 1L).SetProperty(x => x.HeldNanoYuan, 1L), Token);
        var blocked = await f.Service.DeletionEligibilityAsync(mini, Token);
        Assert.Contains("outstanding_balance", blocked.Blockers);
        Assert.Contains("active_usage", blocked.Blockers);
        Assert.Equal("account_deletion_blocked", (await Assert.ThrowsAsync<AccountException>(() =>
            f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, blocked.DeletionVersion), Token))).Code);
        await db.AccountWallets.Where(x => x.AccountId == accountId).ExecuteUpdateAsync(s =>
            s.SetProperty(x => x.OutstandingNanoYuan, 0L).SetProperty(x => x.HeldNanoYuan, 0L), Token);
        var refund = new AccountRefundEntity { Id = Guid.NewGuid().ToString("N"), OrderId = order.OrderId, PlatformRefundId = Guid.NewGuid().ToString("N"), RefundFen = 1, State = "processing", UpdatedAt = DateTimeOffset.UtcNow };
        db.AccountRefunds.Add(refund); await db.SaveChangesAsync(Token);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
        refund.State = "completed"; refund.Applied = true; await db.SaveChangesAsync(Token);
        var allowed = await f.Service.DeletionEligibilityAsync(mini, Token);
        Assert.True(allowed.CanDelete);
        Assert.True((await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, allowed.DeletionVersion), Token)).Deleted);
        Assert.True(await db.AccountRefunds.AnyAsync(x => x.Id == refund.Id && x.State == "completed", Token));
    }

    [Fact, Trait("Category", "Integration")]
    public async Task DeletionCancelsUnissuedPurchasesAndRecoversALostCommitAcknowledgement()
    {
        var fault = new LostCommitAcknowledgement(); var f = Fixture(fault); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var state = await f.Service.DeletionEligibilityAsync(mini, Token);
        Assert.True(state.CanDelete);
        Assert.Null(state.MembershipTier);
        fault.Arm();
        Assert.True((await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, state.DeletionVersion), Token)).Deleted);
        await using var db = new SnowShotDbContext(f.Options);
        Assert.Equal("expired", await db.AccountPurchaseRequests.Where(x => x.Id == purchase.RequestId).Select(x => x.State).SingleAsync(Token));
        Assert.False(await db.AccountPayerClaims.AnyAsync(x => x.OpenId == mini.OpenId, Token));
        Assert.Equal("invalid_session", (await Assert.ThrowsAsync<AccountException>(() => f.Service.MiniProgramPurchaseAsync(mini, Token))).Code);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task IssuingPaymentAndDeletingAnAccountCannotBothSucceed()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var state = await f.Service.DeletionEligibilityAsync(mini, Token);
        async Task<bool> Delete()
        {
            try { return (await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, state.DeletionVersion), Token)).Deleted; }
            catch (AccountException e) when (e.Code is "account_deletion_blocked" or "deletion_state_changed") { return false; }
        }
        async Task<bool> Order()
        {
            try { await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token); return true; }
            catch (AccountException e) when (e.Code is "invalid_session" or "request_not_found" or "request_expired") { return false; }
        }
        var outcomes = await Task.WhenAll(Delete(), Order());
        Assert.NotEqual(outcomes[0], outcomes[1]);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task FreeInFlightRecognitionAndDeletionAreSerializedThroughTheWallet()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        await PayDirect(f, mini, await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-188", Token), 18800);
        await using var db = new SnowShotDbContext(f.Options);
        var accountId = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        var principal = Guid.CreateVersion7();
        db.Principals.Add(new() { Id = principal, CreatedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(Token);
        var policy = ServicePolicy.Defaults(); var resource = policy.Get(Resources.TableExtraction);
        var ledger = new PostgresOperationLedger(new DistributedIntegrationTests.ContextFactory(f.Options), policy);
        var operation = new ReserveOperation(Guid.CreateVersion7(), principal, UsageKind.TableExtraction,
            new(policy.Revision, policy.Fingerprint, resource.Resource, resource.Price, policy.PrincipalDailyAllowance,
                new NanoYuan(1), new NanoYuan(1), StrictPublicReservation: true),
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30))
        { AccountId = accountId };
        var before = await f.Service.DeletionEligibilityAsync(mini, Token);
        async Task<bool> Delete()
        {
            try { return (await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, before.DeletionVersion), Token)).Deleted; }
            catch (AccountException e) when (e.Code is "account_deletion_blocked" or "deletion_state_changed") { return false; }
        }
        var deleting = Delete();
        var reserving = ledger.ReserveAsync(operation, Token);
        await Task.WhenAll(deleting, reserving);
        var deleted = await deleting; var reserved = await reserving;
        Assert.NotEqual(deleted, reserved.Accepted);
        if (reserved.Accepted)
        {
            Assert.True(reserved.Handle!.Snapshot.MembershipRecognitionFree);
            Assert.Equal(NanoYuan.Zero, reserved.Handle.Snapshot.PublicReservation);
            Assert.Contains("active_usage", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
        }
    }

    [Theory, Trait("Category", "Integration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TemporaryAccountAdoptionAndDeletionEraseBothCurrentAndLegacyCredentialCopies(bool upgradeHashFirst)
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        await PayDirect(f, mini, await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token), 990);
        var registration = await Register(f, Name());
        await using var db = new SnowShotDbContext(f.Options);
        var sourceId = await db.AccountPurchaseRequests.Where(x => x.Id == registration.RequestId).Select(x => x.AccountId).SingleAsync(Token);
        await f.Service.LinkRegistrationAsync(mini, registration.RequestId, Token);
        var source = await db.Accounts.SingleAsync(x => x.Id == sourceId, Token);
        Assert.Empty(source.PasswordHash);
        Assert.NotEqual(registration.Username, source.Username);
        var activeId = await db.AccountWechatLinks.Where(x => x.OpenId == mini.OpenId).Select(x => x.AccountId).SingleAsync(Token);
        var active = await db.Accounts.SingleAsync(x => x.Id == activeId, Token);
        // Recreate a legacy orphan produced before the merge cleanup existed.
        source.Username = active.Username; source.NormalizedUsername = active.NormalizedUsername;
        if (upgradeHashFirst)
        {
            var oldHasher = new PasswordHasher<AccountEntity>(Microsoft.Extensions.Options.Options.Create(new PasswordHasherOptions { IterationCount = 10_000 }));
            active.PasswordHash = oldHasher.HashPassword(active, "password-123");
        }
        source.PasswordHash = active.PasswordHash;
        await db.SaveChangesAsync(Token);
        if (upgradeHashFirst)
        {
            await f.Service.LoginAsync(registration.Username!, "password-123", Name(), Token);
            await db.Entry(source).ReloadAsync(Token);
            Assert.Empty(source.PasswordHash);
            Assert.NotEqual(registration.Username, source.Username);
        }
        var state = await f.Service.DeletionEligibilityAsync(mini, Token);
        await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, state.DeletionVersion), Token);
        await db.Entry(source).ReloadAsync(Token); await db.Entry(active).ReloadAsync(Token);
        Assert.Empty(source.PasswordHash); Assert.Empty(active.PasswordHash);
        Assert.NotEqual(registration.Username, source.Username); Assert.NotEqual(registration.Username, active.Username);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task QueuedRefundBlocksDeletionAndConcurrentRefundInboxInsertionWaitsForItsCommit()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var order = await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token);
        await PayDirect(f, mini, order, 990);
        var payload = JsonSerializer.Serialize(new { Event = "xpay_refund_notify", MchOrderId = order.OrderId, OpenId = mini.OpenId });
        await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
        Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
        await using (var db = new SnowShotDbContext(f.Options))
            await db.AccountPlatformEvents.Where(x => x.Id == Convert.ToHexStringLower(AccountSecrets.Hash(payload)))
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "processed"), Token);
        var state = await f.Service.DeletionEligibilityAsync(mini, Token);
        var barrier = new RefundInboxBarrier();
        var options = new DbContextOptionsBuilder<SnowShotDbContext>(f.Options).AddInterceptors(barrier).Options;
        var guarded = f with { Options = options, Service = AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(options), f.Platform, f.Secrets, TimeProvider.System, new()) };
        var deleting = guarded.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, state.DeletionVersion), Token);
        await barrier.InboxRead.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
        var callback = guarded.Service.VerifyCallbackAsync("", "", "", null, " " + payload, "", Token);
        try
        {
            await barrier.ConcurrentLockAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(callback.IsCompleted);
        }
        finally { barrier.Release.TrySetResult(); }
        Assert.True((await deleting).Deleted);
        await callback;
        await using var check = new SnowShotDbContext(f.Options);
        var id = await check.AccountPaymentOrders.Where(x => x.Id == order.OrderId).Select(x => x.AccountId).SingleAsync(Token);
        Assert.True(await check.Accounts.AnyAsync(x => x.Id == id && !x.Active && x.DeletedAt != null, Token));
    }

    private sealed class RefundInboxBarrier : DbCommandInterceptor
    {
        public TaskCompletionSource InboxRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ConcurrentLockAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("account_platform_events", StringComparison.Ordinal) &&
                command.CommandText.Contains("EXISTS", StringComparison.Ordinal) && command.CommandText.Contains("MerchantOrderId", StringComparison.Ordinal))
            {
                InboxRead.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (InboxRead.Task.IsCompleted && command.CommandText.Contains("pg_advisory_xact_lock", StringComparison.Ordinal))
                ConcurrentLockAttempt.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
