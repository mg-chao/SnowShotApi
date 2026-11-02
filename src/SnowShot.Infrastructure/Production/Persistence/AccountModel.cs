using Microsoft.EntityFrameworkCore;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

namespace SnowShot.Infrastructure.Persistence;

public sealed partial class SnowShotDbContext
{
    internal DbSet<AccountEntity> Accounts => Set<AccountEntity>();
    internal DbSet<AccountUsernameClaimEntity> AccountUsernameClaims => Set<AccountUsernameClaimEntity>();
    internal DbSet<AccountWalletEntity> AccountWallets => Set<AccountWalletEntity>();
    internal DbSet<AccountFinancialEntryEntity> AccountFinancialEntries => Set<AccountFinancialEntryEntity>();
    internal DbSet<AccountUsageDedupEntity> AccountUsageDeduplications => Set<AccountUsageDedupEntity>();
    internal DbSet<AccountWechatLinkEntity> AccountWechatLinks => Set<AccountWechatLinkEntity>();
    internal DbSet<AccountPayerClaimEntity> AccountPayerClaims => Set<AccountPayerClaimEntity>();
    internal DbSet<AccountSessionEntity> AccountSessions => Set<AccountSessionEntity>();
    internal DbSet<AccountPurchaseRequestEntity> AccountPurchaseRequests => Set<AccountPurchaseRequestEntity>();
    internal DbSet<AccountPaymentOrderEntity> AccountPaymentOrders => Set<AccountPaymentOrderEntity>();
    internal DbSet<AccountRefundEntity> AccountRefunds => Set<AccountRefundEntity>();
    internal DbSet<AccountCaptchaEntity> AccountCaptchas => Set<AccountCaptchaEntity>();
    internal DbSet<AccountThrottleEntity> AccountThrottles => Set<AccountThrottleEntity>();
    internal DbSet<AccountPlatformEventEntity> AccountPlatformEvents => Set<AccountPlatformEventEntity>();

    private static void ConfigureAccountModel(ModelBuilder model)
    {
        model.Entity<AccountEntity>(e => { e.ToTable("accounts"); e.HasKey(x => x.Id); e.Property(x => x.IsTemporary).HasDefaultValue(false); e.Property(x => x.Username).HasMaxLength(32); e.Property(x => x.NormalizedUsername).HasMaxLength(32); });
        model.Entity<AccountUsernameClaimEntity>(e => { e.ToTable("account_username_claims"); e.HasKey(x => x.NormalizedUsername); e.Property(x => x.NormalizedUsername).HasMaxLength(32); e.HasIndex(x => x.AccountId).IsUnique(); e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountWalletEntity>(e => { e.ToTable("account_wallets", t => t.HasCheckConstraint("ck_account_wallet_nonnegative", "\"AvailableNanoYuan\" >= 0 AND \"HeldNanoYuan\" >= 0 AND \"OutstandingNanoYuan\" >= 0 AND \"CumulativeRechargeNanoYuan\" >= 0")); e.Property(x => x.CumulativeRechargeNanoYuan).HasDefaultValue(0L); e.HasKey(x => x.AccountId); e.HasOne<AccountEntity>().WithOne().HasForeignKey<AccountWalletEntity>(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountFinancialEntryEntity>(e => { e.ToTable("account_financial_entries"); e.HasKey(x => x.Id); e.Property(x => x.Id).HasMaxLength(160); e.Property(x => x.Type).HasMaxLength(16); e.Property(x => x.Status).HasMaxLength(16); e.HasIndex(x => new { x.AccountId, x.OccurredAt, x.Id }); e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountUsageDedupEntity>(e => { e.ToTable("account_usage_deduplications"); e.HasKey(x => x.Hash); e.Property(x => x.Hash).HasColumnType("bytea").HasMaxLength(32).ValueGeneratedNever(); e.HasIndex(x => new { x.AccountId, x.OperationId }).IsUnique(); e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountWechatLinkEntity>(e => { e.ToTable("account_wechat_links"); e.HasKey(x => x.OpenId); e.Property(x => x.OpenId).HasMaxLength(128); e.HasIndex(x => x.AccountId).IsUnique(); e.HasOne<AccountEntity>().WithOne().HasForeignKey<AccountWechatLinkEntity>(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountPayerClaimEntity>(e => { e.ToTable("account_payer_claims"); e.HasKey(x => x.AccountId); e.Property(x => x.OpenId).HasMaxLength(128); e.HasIndex(x => x.OpenId).IsUnique(); e.HasOne<AccountEntity>().WithOne().HasForeignKey<AccountPayerClaimEntity>(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountSessionEntity>(e => { e.ToTable("account_sessions"); e.HasKey(x => x.Id); e.HasIndex(x => x.AccessHash).IsUnique(); e.HasIndex(x => x.RefreshHash).IsUnique(); e.HasIndex(x => x.RefreshExpiresAt); e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountPurchaseRequestEntity>(e => { e.ToTable("account_purchase_requests"); e.HasKey(x => x.Id); e.Property(x => x.PayerOpenId).HasMaxLength(128); e.Property(x => x.CompletionKind).HasMaxLength(16); e.HasIndex(x => new { x.PayerOpenId, x.State }); e.HasIndex(x => new { x.State, x.ExpiresAt }); e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountPaymentOrderEntity>(e => { e.ToTable("account_payment_orders"); e.HasKey(x => x.Id); e.HasIndex(x => x.CheckoutAttemptId).IsUnique(); e.HasIndex(x => x.RequestId).IsUnique(); e.HasIndex(x => new { x.State, x.CreatedAt }); e.HasIndex(x => x.PlatformTransactionId).IsUnique(); e.HasIndex(x => new { x.NextDeliveryAttemptAt, x.CreatedAt, x.Id }).HasFilter("\"Fulfilled\" AND NOT \"DeliveryConfirmed\""); e.HasOne<AccountPurchaseRequestEntity>().WithOne().HasForeignKey<AccountPaymentOrderEntity>(x => x.RequestId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountRefundEntity>(e => { e.ToTable("account_refunds"); e.HasKey(x => x.Id); e.HasIndex(x => x.OrderId); e.HasIndex(x => new { x.NextQueryAt, x.UpdatedAt, x.Id }).HasFilter("\"State\" = 'processing'"); e.HasOne<AccountPaymentOrderEntity>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.Restrict); });
        model.Entity<AccountCaptchaEntity>(e => { e.ToTable("account_captchas"); e.HasKey(x => x.Id); e.HasIndex(x => x.ExpiresAt); });
        model.Entity<AccountThrottleEntity>(e => { e.ToTable("account_throttles"); e.HasKey(x => x.Id); });
        model.Entity<AccountPlatformEventEntity>(e =>
        {
            e.ToTable("account_platform_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.MerchantOrderId).HasMaxLength(32);
            e.Property(x => x.RoutingMetadataComplete).HasDefaultValue(false);
            e.HasIndex(x => new { x.State, x.ReceivedAt });
            e.HasIndex(x => x.MerchantOrderId).HasFilter("\"EventName\" = 'xpay_refund_notify' AND \"State\" <> 'processed'");
            e.HasIndex(x => new { x.RoutingLastAttemptAt, x.Id }).HasDatabaseName("ix_account_platform_events_unrouted_refunds")
                .HasNullSortOrder(NullSortOrder.NullsFirst, NullSortOrder.NullsLast)
                .HasFilter("\"EventName\" = 'xpay_refund_notify' AND \"State\" <> 'processed' AND NOT \"RoutingMetadataComplete\"");
        });
        model.Entity<UsageOperationEntity>(e =>
        {
            e.Property(x => x.MembershipRecognitionFree).HasDefaultValue(false);
            e.HasOne<AccountEntity>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable("usage_operations", table => table.HasCheckConstraint("ck_account_usage_funding",
                "\"ReservedFreeNanoYuan\" >= 0 AND \"ReservedWalletNanoYuan\" >= 0 AND (\"AccountId\" IS NULL OR \"ReservedFreeNanoYuan\" + \"ReservedWalletNanoYuan\" = \"ReservedPublicNanoYuan\")"));
        });
    }
}
