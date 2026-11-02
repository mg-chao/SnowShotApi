using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SnowShot.Infrastructure.Accounts;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed partial class AccountLifecycleIntegrationTests
{
    [Fact, Trait("Category", "Integration")]
    public async Task DeletionUsesOnlyItsIndexedRefundEvidenceWithoutDecryptingUnrelatedPayloads()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var order = await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token);
        await PayDirect(f, mini, order, 990);
        var id = Guid.NewGuid().ToString("N");
        await using var db = new SnowShotDbContext(f.Options);
        db.AccountPlatformEvents.Add(new()
        {
            Id = id,
            EventName = "xpay_refund_notify",
            MerchantOrderId = Guid.NewGuid().ToString("N"),
            RoutingMetadataComplete = true,
            PayloadEncrypted = "Unrelated evidence must never be decrypted by deletion.",
            State = "pending",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Token);
        try
        {
            var eligibility = await f.Service.DeletionEligibilityAsync(mini, Token);
            Assert.True(eligibility.CanDelete);
            Assert.True((await f.Service.DeleteAsync(mini, new("DELETE", mini.OpenId!, true, eligibility.DeletionVersion), Token)).Deleted);
        }
        finally { await db.AccountPlatformEvents.Where(x => x.Id == id).ExecuteDeleteAsync(Token); }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task LegacyRefundRoutingIsBackfilledBeforeTakingIdentityOrWalletLocks()
    {
        var f = Fixture(); var mini = await Mini(f);
        var purchase = await f.Service.MiniProgramPurchaseAsync(mini, Token);
        var order = await f.Service.CreateOrderAsync(mini, purchase.RequestId, "cny-9-9", Token);
        await PayDirect(f, mini, order, 990);
        var id = Guid.NewGuid().ToString("N");
        await using var db = new SnowShotDbContext(f.Options);
        db.AccountPlatformEvents.Add(new()
        {
            Id = id,
            EventName = "xpay_refund_notify",
            PayloadEncrypted = f.Secrets.Protect(JsonSerializer.Serialize(new { MchOrderId = order.OrderId })),
            State = "pending",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Token);
        var probe = new RefundRoutingLockProbe();
        var options = new DbContextOptionsBuilder<SnowShotDbContext>(f.Options).AddInterceptors(probe).Options;
        var guarded = AccountTestServices.Create(new DistributedIntegrationTests.ContextFactory(options), f.Platform, f.Secrets, TimeProvider.System, new());
        await using var identity = new SnowShotDbContext(f.Options);
        await using var transaction = await identity.Database.BeginTransactionAsync(Token);
        await AccountSynchronization.ChangeIdentityAsync(identity, Token);
        var eligibility = guarded.DeletionEligibilityAsync(mini, Token);
        try
        {
            await probe.IdentityLockAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10), Token);
            Assert.False(eligibility.IsCompleted);
            var indexed = await db.AccountPlatformEvents.AsNoTracking().SingleAsync(x => x.Id == id, Token);
            Assert.True(indexed.RoutingMetadataComplete);
            Assert.Equal(order.OrderId, indexed.MerchantOrderId);
        }
        finally { await transaction.CommitAsync(Token); }
        Assert.Contains("pending_refund", (await eligibility).Blockers);
        await db.AccountPlatformEvents.Where(x => x.Id == id).ExecuteUpdateAsync(set => set.SetProperty(x => x.State, "processed"), Token);
        Assert.True((await f.Service.DeletionEligibilityAsync(mini, Token)).CanDelete);
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UnroutedLegacyRefundsBlockDeletionUntilBoundedBatchesKnowTheirOrders()
    {
        var f = Fixture(); var mini = await Mini(f);
        await using var db = new SnowShotDbContext(f.Options);
        var ids = Enumerable.Range(0, 101).Select(_ => Guid.NewGuid().ToString("N")).ToArray();
        foreach (var id in ids)
            db.AccountPlatformEvents.Add(new()
            {
                Id = id,
                EventName = "xpay_refund_notify",
                PayloadEncrypted = f.Secrets.Protect(JsonSerializer.Serialize(new { MchOrderId = Guid.NewGuid().ToString("N") })),
                State = "pending",
                ReceivedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync(Token);
        try
        {
            var first = await f.Service.DeletionEligibilityAsync(mini, Token);
            Assert.Contains("pending_refund", first.Blockers);
            Assert.Equal(100, await db.AccountPlatformEvents.CountAsync(x => ids.Contains(x.Id) && x.RoutingMetadataComplete, Token));
            Assert.True((await f.Service.DeletionEligibilityAsync(mini, Token)).CanDelete);
            Assert.Equal(101, await db.AccountPlatformEvents.CountAsync(x => ids.Contains(x.Id) && x.RoutingMetadataComplete, Token));
        }
        finally { await db.AccountPlatformEvents.Where(x => ids.Contains(x.Id)).ExecuteDeleteAsync(Token); }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task UnreadableLegacyEvidenceRemainsGuardedWithoutPreventingOtherRoutingOrReconciliation()
    {
        var f = Fixture(); var mini = await Mini(f);
        var unreadableId = Guid.NewGuid().ToString("N"); var validId = Guid.NewGuid().ToString("N");
        await using var db = new SnowShotDbContext(f.Options);
        db.AccountPlatformEvents.Add(new()
        {
            Id = unreadableId,
            EventName = "xpay_refund_notify",
            PayloadEncrypted = "unreadable",
            State = "pending",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        db.AccountPlatformEvents.Add(new()
        {
            Id = validId,
            EventName = "xpay_refund_notify",
            PayloadEncrypted = f.Secrets.Protect(JsonSerializer.Serialize(new { MchOrderId = Guid.NewGuid().ToString("N") })),
            State = "pending",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Token);
        try
        {
            Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
            Assert.False(await db.AccountPlatformEvents.Where(x => x.Id == unreadableId).Select(x => x.RoutingMetadataComplete).SingleAsync(Token));
            Assert.True(await db.AccountPlatformEvents.Where(x => x.Id == validId).Select(x => x.RoutingMetadataComplete).SingleAsync(Token));
            await f.Service.ReconcileAsync(Token);
            Assert.True(await db.AccountPlatformEvents.AnyAsync(x => x.Id == unreadableId && x.State == "pending", Token));
        }
        finally { await db.AccountPlatformEvents.Where(x => x.Id == unreadableId || x.Id == validId).ExecuteDeleteAsync(Token); }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task MoreThanOneBatchOfUnreadableEvidenceCannotStarveLaterRecoverableRouting()
    {
        var f = Fixture(); var mini = await Mini(f);
        var unreadableIds = Enumerable.Range(0, 101).Select(_ => "0" + Guid.NewGuid().ToString("N")[1..]).ToArray();
        var validId = "f" + Guid.NewGuid().ToString("N")[1..];
        var orderId = Guid.NewGuid().ToString("N");
        await using var db = new SnowShotDbContext(f.Options);
        foreach (var id in unreadableIds)
            db.AccountPlatformEvents.Add(new()
            {
                Id = id,
                EventName = "xpay_refund_notify",
                PayloadEncrypted = "unreadable",
                State = "pending",
                ReceivedAt = DateTimeOffset.UtcNow
            });
        db.AccountPlatformEvents.Add(new()
        {
            Id = validId,
            EventName = "xpay_refund_notify",
            PayloadEncrypted = f.Secrets.Protect(JsonSerializer.Serialize(new { MchOrderId = orderId })),
            State = "pending",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Token);
        try
        {
            Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
            Assert.False(await db.AccountPlatformEvents.Where(x => x.Id == validId).Select(x => x.RoutingMetadataComplete).SingleAsync(Token));
            Assert.Contains("pending_refund", (await f.Service.DeletionEligibilityAsync(mini, Token)).Blockers);
            var recovered = await db.AccountPlatformEvents.AsNoTracking().SingleAsync(x => x.Id == validId, Token);
            Assert.True(recovered.RoutingMetadataComplete);
            Assert.Equal(orderId, recovered.MerchantOrderId);
            Assert.Equal(101, await db.AccountPlatformEvents.CountAsync(x => unreadableIds.Contains(x.Id) && !x.RoutingMetadataComplete, Token));
            Assert.All(await db.AccountPlatformEvents.AsNoTracking().Where(x => unreadableIds.Contains(x.Id)).ToListAsync(Token), entry => Assert.NotNull(entry.RoutingLastAttemptAt));
        }
        finally { await db.AccountPlatformEvents.Where(x => x.Id == validId || unreadableIds.Contains(x.Id)).ExecuteDeleteAsync(Token); }
    }

    [Fact, Trait("Category", "Integration")]
    public async Task CallbackRedeliveryUpgradesRetainedLegacyRoutingWithoutRepeatingTheInboxRecord()
    {
        var f = Fixture();
        var orderId = Guid.NewGuid().ToString("N");
        var payload = JsonSerializer.Serialize(new { Event = "xpay_refund_notify", MchOrderId = orderId });
        var id = Convert.ToHexStringLower(AccountSecrets.Hash(payload));
        await using var db = new SnowShotDbContext(f.Options);
        db.AccountPlatformEvents.Add(new()
        {
            Id = id,
            EventName = "xpay_refund_notify",
            PayloadEncrypted = f.Secrets.Protect(payload),
            State = "pending",
            ReceivedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(Token);
        try
        {
            await f.Service.VerifyCallbackAsync("", "", "", null, payload, "", Token);
            var entry = await db.AccountPlatformEvents.AsNoTracking().SingleAsync(x => x.Id == id, Token);
            Assert.True(entry.RoutingMetadataComplete);
            Assert.Equal(orderId, entry.MerchantOrderId);
            Assert.Equal("pending", entry.State);
            Assert.Single(await db.AccountPlatformEvents.Where(x => x.Id == id).ToListAsync(Token));
        }
        finally { await db.AccountPlatformEvents.Where(x => x.Id == id).ExecuteDeleteAsync(Token); }
    }

    private sealed class RefundRoutingLockProbe : DbCommandInterceptor
    {
        public TaskCompletionSource IdentityLockAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("pg_advisory_xact_lock_shared", StringComparison.Ordinal)) IdentityLockAttempt.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }
}
