using Microsoft.EntityFrameworkCore;

namespace SnowShot.Infrastructure.Persistence;

public sealed class SnowShotDbContext(DbContextOptions<SnowShotDbContext> options) : DbContext(options)
{
    public const string Schema = "snowshot";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    internal DbSet<PrincipalEntity> Principals => Set<PrincipalEntity>();
    internal DbSet<PrincipalFingerprintEntity> PrincipalFingerprints => Set<PrincipalFingerprintEntity>();
    internal DbSet<AllowancePeriodEntity> AllowancePeriods => Set<AllowancePeriodEntity>();
    internal DbSet<OperatorBudgetPeriodEntity> OperatorBudgetPeriods => Set<OperatorBudgetPeriodEntity>();
    internal DbSet<UsageOperationEntity> UsageOperations => Set<UsageOperationEntity>();
    internal DbSet<ProviderAttemptEntity> ProviderAttempts => Set<ProviderAttemptEntity>();
    internal DbSet<UsageEventEntity> UsageEvents => Set<UsageEventEntity>();
    internal DbSet<DailyAggregateEntity> DailyAggregates => Set<DailyAggregateEntity>();
    internal DbSet<PolicyRevisionEntity> PolicyRevisions => Set<PolicyRevisionEntity>();
    internal DbSet<PolicyStateEntity> PolicyStates => Set<PolicyStateEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<PrincipalEntity>(entity =>
        {
            entity.ToTable("principals");
            entity.HasKey(value => value.Id);
            entity.HasIndex(value => value.CreatedAt).HasDatabaseName("ix_principals_retention");
        });
        modelBuilder.Entity<PrincipalFingerprintEntity>(entity =>
        {
            entity.ToTable("principal_fingerprints", table =>
                table.HasCheckConstraint("ck_principal_fingerprint_length", "octet_length(\"Fingerprint\") = 32"));
            entity.HasKey(value => value.Fingerprint);
            entity.Property(value => value.Fingerprint).HasColumnType("bytea").HasMaxLength(32).ValueGeneratedNever();
            entity.HasIndex(value => value.PrincipalId).HasDatabaseName("ix_principal_fingerprints_principal");
            entity.HasIndex(value => value.LastSeenAt).HasDatabaseName("ix_principal_fingerprints_retention");
            entity.HasOne<PrincipalEntity>().WithMany().HasForeignKey(value => value.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<AllowancePeriodEntity>(entity =>
        {
            entity.ToTable("allowance_periods", table =>
            {
                table.HasCheckConstraint("ck_allowance_nonnegative", "\"LimitNanoYuan\" > 0 AND \"CommittedNanoYuan\" >= 0 AND \"ReservedNanoYuan\" >= 0 AND \"AppliedPolicyRevision\" > 0");
            });
            entity.HasKey(value => new { value.PrincipalId, value.PeriodDate });
            entity.HasIndex(value => value.PeriodDate).HasDatabaseName("ix_allowance_periods_retention");
            entity.HasOne<PrincipalEntity>().WithMany().HasForeignKey(value => value.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<OperatorBudgetPeriodEntity>(entity =>
        {
            entity.ToTable("operator_budget_periods", table =>
            {
                table.HasCheckConstraint("ck_operator_period_kind", "\"Kind\" IN (0, 1)");
                table.HasCheckConstraint("ck_operator_budget_nonnegative", "\"LimitNanoYuan\" > 0 AND \"CommittedNanoYuan\" >= 0 AND \"ReservedNanoYuan\" >= 0 AND \"AppliedPolicyRevision\" > 0");
            });
            entity.HasKey(value => new { value.Kind, value.PeriodKey });
            entity.Property(value => value.PeriodKey).HasMaxLength(8);
            entity.HasIndex(value => value.UpdatedAt).HasDatabaseName("ix_operator_budget_periods_retention");
        });
        modelBuilder.Entity<UsageOperationEntity>(entity =>
        {
            entity.ToTable("usage_operations", table =>
            {
                table.HasCheckConstraint("ck_usage_operation_kind", "\"Kind\" IN (0, 1, 2)");
                table.HasCheckConstraint("ck_usage_operation_state", "\"State\" BETWEEN 0 AND 4");
                table.HasCheckConstraint("ck_usage_operation_hashes", "octet_length(\"IdempotencyHash\") = 32 AND octet_length(\"OwnerToken\") = 32 AND octet_length(\"PolicyFingerprint\") = 32 AND \"PolicyRevision\" > 0 AND (\"SettlementFingerprint\" IS NULL OR octet_length(\"SettlementFingerprint\") = 32)");
                table.HasCheckConstraint("ck_usage_operation_fence", "\"Fence\" > 0");
                table.HasCheckConstraint("ck_usage_operation_costs", "\"InputRateNanoYuan\" >= 0 AND \"OutputRateNanoYuan\" >= 0 AND \"ReservedPublicNanoYuan\" >= 0 AND \"ReservedOperatorNanoYuan\" >= 0 AND \"ActualPublicNanoYuan\" >= 0 AND \"ActualOperatorNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0");
                table.HasCheckConstraint("ck_usage_operation_deadline", "\"CreatedAt\" < \"AbsoluteDeadline\" AND \"LeaseExpiresAt\" <= \"AbsoluteDeadline\"");
                table.HasCheckConstraint("ck_usage_operation_terminal", "(\"State\" IN (0, 1) AND \"SettledAt\" IS NULL AND \"SettlementFingerprint\" IS NULL) OR (\"State\" IN (2, 3, 4) AND \"SettledAt\" IS NOT NULL AND \"SettlementFingerprint\" IS NOT NULL)");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Resource).HasMaxLength(64);
            entity.Property(value => value.IdempotencyHash).HasColumnType("bytea").HasMaxLength(32);
            entity.Property(value => value.OwnerToken).HasColumnType("bytea").HasMaxLength(32);
            entity.Property(value => value.PolicyFingerprint).HasColumnType("bytea").HasMaxLength(32);
            entity.Property(value => value.SettlementFingerprint).HasColumnType("bytea").HasMaxLength(32);
            entity.HasIndex(value => value.IdempotencyHash).IsUnique().HasDatabaseName("uq_usage_operations_idempotency_hash");
            entity.HasIndex(value => new { value.State, value.LeaseExpiresAt }).HasDatabaseName("ix_usage_operations_reconciliation");
            entity.HasIndex(value => new { value.State, value.SettledAt }).HasDatabaseName("ix_usage_operations_retention");
            entity.HasIndex(value => new { value.PrincipalId, value.AllowanceDate }).HasDatabaseName("ix_usage_operations_principal_period");
            entity.HasOne<PrincipalEntity>().WithMany().HasForeignKey(value => value.PrincipalId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<ProviderAttemptEntity>(entity =>
        {
            entity.ToTable("provider_attempts", table =>
            {
                table.HasCheckConstraint("ck_provider_attempt_values", "\"AttemptNumber\" > 0 AND \"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"CostNanoYuan\" >= 0 AND (\"CostKnown\" OR \"CostNanoYuan\" = 0)");
                table.HasCheckConstraint("ck_provider_attempt_state", "\"State\" IN (0, 1) AND \"DispatchState\" BETWEEN 0 AND 3");
                table.HasCheckConstraint("ck_provider_attempt_lifecycle", "(\"State\" = 0 AND \"DispatchState\" = 0 AND \"CompletedAt\" IS NULL AND \"Outcome\" IS NULL AND \"HttpStatus\" IS NULL AND \"InputUnits\" = 0 AND \"OutputUnits\" = 0 AND \"CostNanoYuan\" = 0 AND NOT \"CostKnown\") OR (\"State\" = 1 AND \"DispatchState\" IN (1, 2, 3) AND \"CompletedAt\" IS NOT NULL AND \"CompletedAt\" >= \"StartedAt\" AND \"Outcome\" IS NOT NULL AND (\"DispatchState\" <> 1 OR (\"CostKnown\" AND \"CostNanoYuan\" = 0)))");
            });
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Provider).HasMaxLength(64);
            entity.Property(value => value.Resource).HasMaxLength(64);
            entity.Property(value => value.Outcome).HasMaxLength(64);
            entity.HasIndex(value => new { value.OperationId, value.AttemptNumber }).IsUnique().HasDatabaseName("uq_provider_attempt_operation_number");
            entity.HasIndex(value => value.CompletedAt).HasDatabaseName("ix_provider_attempts_retention");
            entity.HasOne<UsageOperationEntity>().WithMany().HasForeignKey(value => value.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<UsageEventEntity>(entity =>
        {
            entity.ToTable("usage_events", table => table.HasCheckConstraint("ck_usage_event_values", "\"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"PublicCostNanoYuan\" >= 0 AND \"OperatorCostNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0"));
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).UseIdentityByDefaultColumn();
            entity.Property(value => value.Resource).HasMaxLength(64);
            entity.Property(value => value.Outcome).HasMaxLength(64);
            entity.HasIndex(value => value.OperationId).IsUnique().HasDatabaseName("uq_usage_events_operation");
            entity.HasIndex(value => value.OccurredAt).HasDatabaseName("ix_usage_events_retention");
            entity.HasOne<UsageOperationEntity>().WithMany().HasForeignKey(value => value.OperationId).OnDelete(DeleteBehavior.Restrict);
        });
        modelBuilder.Entity<DailyAggregateEntity>(entity =>
        {
            entity.ToTable("daily_aggregates");
            entity.HasKey(value => new { value.UsageDate, value.Kind, value.Resource });
            entity.Property(value => value.Resource).HasMaxLength(64);
            entity.HasIndex(value => value.UsageDate).HasDatabaseName("ix_daily_aggregates_retention");
        });
        modelBuilder.Entity<PolicyRevisionEntity>(entity =>
        {
            entity.ToTable("policy_revisions", table =>
            {
                table.HasCheckConstraint("ck_policy_revision_positive", "\"Revision\" > 0");
                table.HasCheckConstraint("ck_policy_revision_fingerprint", "octet_length(\"Fingerprint\") = 32");
                table.HasCheckConstraint("ck_policy_revision_caps", "\"PrincipalDailyAllowanceNanoYuan\" > 0 AND \"DailyOperatorBudgetNanoYuan\" > 0 AND \"MonthlyOperatorBudgetNanoYuan\" > 0");
            });
            entity.HasKey(value => value.Revision);
            entity.Property(value => value.Revision).ValueGeneratedNever();
            entity.Property(value => value.Fingerprint).HasColumnType("bytea").HasMaxLength(32);
            entity.Property(value => value.CanonicalDocument).HasColumnType("text");
            entity.HasIndex(value => value.Fingerprint).HasDatabaseName("ix_policy_revisions_fingerprint");
        });
        modelBuilder.Entity<PolicyStateEntity>(entity =>
        {
            entity.ToTable("policy_state", table => table.HasCheckConstraint("ck_policy_state_singleton", "\"Id\" = 1"));
            entity.HasKey(value => value.Id);
            entity.Property(value => value.Id).ValueGeneratedNever();
            entity.HasOne<PolicyRevisionEntity>().WithMany().HasForeignKey(value => value.ActiveRevision).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
