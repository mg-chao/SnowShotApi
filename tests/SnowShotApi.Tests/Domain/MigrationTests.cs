using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SnowShot.Infrastructure.Persistence;

namespace SnowShotApi.Tests.Domain;

public sealed class MigrationTests
{
    [Fact]
    public void CurrentModelMatchesMigrations()
    {
        using var context = Context();
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Equal(14, context.Database.GetMigrations().Count());
    }

    [Fact]
    public void MigrationContainsAccountingAndRetentionInvariants()
    {
        using var context = Context();
        var script = context.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.DoesNotContain("ck_allowance_limit", script, StringComparison.Ordinal);
        Assert.Contains("policy_revisions", script, StringComparison.Ordinal);
        Assert.Contains("policy_state", script, StringComparison.Ordinal);
        Assert.Contains("AppliedPolicyRevision", script, StringComparison.Ordinal);
        Assert.Contains("uq_usage_operations_idempotency_hash", script, StringComparison.Ordinal);
        Assert.Contains("uq_usage_events_operation", script, StringComparison.Ordinal);
        Assert.Contains("ix_usage_operations_reconciliation", script, StringComparison.Ordinal);
        Assert.Contains("ix_usage_operations_retention", script, StringComparison.Ordinal);
        Assert.Contains("ix_allowance_periods_retention", script, StringComparison.Ordinal);
        Assert.Contains("ix_operator_budget_periods_retention", script, StringComparison.Ordinal);
        Assert.Contains("ix_principals_retention", script, StringComparison.Ordinal);
        Assert.Contains("ix_usage_events_retention", script, StringComparison.Ordinal);
        Assert.Contains("principal_fingerprints", script, StringComparison.Ordinal);
        Assert.Contains("ck_usage_operation_fence", script, StringComparison.Ordinal);
        Assert.Contains("ck_usage_operation_terminal", script, StringComparison.Ordinal);
        Assert.Contains("ADD \"ModelPricingSnapshot\" text", script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"ModelPricingSnapshot\" text NOT NULL", script, StringComparison.Ordinal);
        Assert.Contains("account_wallets", script, StringComparison.Ordinal);
        Assert.Contains("account_usage_deduplications", script, StringComparison.Ordinal);
        Assert.Contains("account_platform_events", script, StringComparison.Ordinal);
        Assert.Contains("ck_account_wallet_nonnegative", script, StringComparison.Ordinal);
        Assert.Contains("ck_account_usage_funding", script, StringComparison.Ordinal);
        Assert.Contains("DeliveryConfirmed", script, StringComparison.Ordinal);
        Assert.Contains("ValidationReceipt", script, StringComparison.Ordinal);
        Assert.Contains("CumulativeRechargeNanoYuan", script, StringComparison.Ordinal);
        Assert.Contains("MembershipRecognitionFree", script, StringComparison.Ordinal);
        Assert.Contains("IsTemporary", script, StringComparison.Ordinal);
        Assert.Contains("PayerOpenId", script, StringComparison.Ordinal);
        Assert.Contains("CompletionKind", script, StringComparison.Ordinal);
        Assert.Contains("DeletedAt", script, StringComparison.Ordinal);
        Assert.Contains("DeletionConsentVersion", script, StringComparison.Ordinal);
        Assert.Contains("NextDeliveryAttemptAt", script, StringComparison.Ordinal);
        Assert.Contains("DeliveryLeaseToken", script, StringComparison.Ordinal);
        Assert.Contains("NextQueryAt", script, StringComparison.Ordinal);
        Assert.Contains("QueryLeaseToken", script, StringComparison.Ordinal);
        Assert.Contains("CheckoutAttemptId", script, StringComparison.Ordinal);
        Assert.Contains("MerchantOrderId", script, StringComparison.Ordinal);
        Assert.Contains("RoutingMetadataComplete", script, StringComparison.Ordinal);
        Assert.Contains("ix_account_platform_events_unrouted_refunds", script, StringComparison.Ordinal);
    }

    private static SnowShotDbContext Context() => new(new DbContextOptionsBuilder<SnowShotDbContext>()
        .UseNpgsql("Host=127.0.0.1;Database=unused;Username=unused;Password=unused", npgsql => npgsql
            .MigrationsHistoryTable(SnowShotDbContext.MigrationsHistoryTable, SnowShotDbContext.Schema)).Options);
}
