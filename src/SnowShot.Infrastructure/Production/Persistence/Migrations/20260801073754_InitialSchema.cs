using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration metadata uses inline column arrays.
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "snowshot");

            migrationBuilder.CreateTable(
                name: "daily_aggregates",
                schema: "snowshot",
                columns: table => new
                {
                    UsageDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Requests = table.Column<long>(type: "bigint", nullable: false),
                    UnknownCostRequests = table.Column<long>(type: "bigint", nullable: false),
                    InputUnits = table.Column<long>(type: "bigint", nullable: false),
                    OutputUnits = table.Column<long>(type: "bigint", nullable: false),
                    PublicCostNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OperatorCostNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OperatorOverageNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_aggregates", x => new { x.UsageDate, x.Kind, x.Resource });
                });

            migrationBuilder.CreateTable(
                name: "operator_budget_periods",
                schema: "snowshot",
                columns: table => new
                {
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    PeriodKey = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    LimitNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    CommittedNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ReservedNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    AppliedPolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operator_budget_periods", x => new { x.Kind, x.PeriodKey });
                    table.CheckConstraint("ck_operator_budget_nonnegative", "\"LimitNanoYuan\" > 0 AND \"CommittedNanoYuan\" >= 0 AND \"ReservedNanoYuan\" >= 0 AND \"AppliedPolicyRevision\" > 0");
                    table.CheckConstraint("ck_operator_period_kind", "\"Kind\" IN (0, 1)");
                });

            migrationBuilder.CreateTable(
                name: "policy_revisions",
                schema: "snowshot",
                columns: table => new
                {
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    CanonicalDocument = table.Column<string>(type: "text", nullable: false),
                    PrincipalDailyAllowanceNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    DailyOperatorBudgetNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    MonthlyOperatorBudgetNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_revisions", x => x.Revision);
                    table.CheckConstraint("ck_policy_revision_caps", "\"PrincipalDailyAllowanceNanoYuan\" > 0 AND \"DailyOperatorBudgetNanoYuan\" > 0 AND \"MonthlyOperatorBudgetNanoYuan\" > 0");
                    table.CheckConstraint("ck_policy_revision_fingerprint", "octet_length(\"Fingerprint\") = 32");
                    table.CheckConstraint("ck_policy_revision_positive", "\"Revision\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "principals",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_principals", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "policy_state",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<short>(type: "smallint", nullable: false),
                    ActiveRevision = table.Column<long>(type: "bigint", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_policy_state", x => x.Id);
                    table.CheckConstraint("ck_policy_state_singleton", "\"Id\" = 1");
                    table.ForeignKey(
                        name: "FK_policy_state_policy_revisions_ActiveRevision",
                        column: x => x.ActiveRevision,
                        principalSchema: "snowshot",
                        principalTable: "policy_revisions",
                        principalColumn: "Revision",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "allowance_periods",
                schema: "snowshot",
                columns: table => new
                {
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    PeriodDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LimitNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    CommittedNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ReservedNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    AppliedPolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allowance_periods", x => new { x.PrincipalId, x.PeriodDate });
                    table.CheckConstraint("ck_allowance_nonnegative", "\"LimitNanoYuan\" > 0 AND \"CommittedNanoYuan\" >= 0 AND \"ReservedNanoYuan\" >= 0 AND \"AppliedPolicyRevision\" > 0");
                    table.ForeignKey(
                        name: "FK_allowance_periods_principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalSchema: "snowshot",
                        principalTable: "principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "principal_fingerprints",
                schema: "snowshot",
                columns: table => new
                {
                    Fingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_principal_fingerprints", x => x.Fingerprint);
                    table.CheckConstraint("ck_principal_fingerprint_length", "octet_length(\"Fingerprint\") = 32");
                    table.ForeignKey(
                        name: "FK_principal_fingerprints_principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalSchema: "snowshot",
                        principalTable: "principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_operations",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllowanceDate = table.Column<DateOnly>(type: "date", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyHash = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    OwnerToken = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    Fence = table.Column<long>(type: "bigint", nullable: false),
                    PolicyFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: false),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    InputRateNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OutputRateNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    AllowanceLimitNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ReservedPublicNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ReservedOperatorNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ActualPublicNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    ActualOperatorNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OperatorOverageNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AbsoluteDeadline = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SettlementFingerprint = table.Column<byte[]>(type: "bytea", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_operations", x => x.Id);
                    table.CheckConstraint("ck_usage_operation_costs", "\"InputRateNanoYuan\" >= 0 AND \"OutputRateNanoYuan\" >= 0 AND \"ReservedPublicNanoYuan\" >= 0 AND \"ReservedOperatorNanoYuan\" >= 0 AND \"ActualPublicNanoYuan\" >= 0 AND \"ActualOperatorNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0 AND \"ActualPublicNanoYuan\" <= \"ReservedPublicNanoYuan\"");
                    table.CheckConstraint("ck_usage_operation_deadline", "\"CreatedAt\" < \"AbsoluteDeadline\" AND \"LeaseExpiresAt\" <= \"AbsoluteDeadline\"");
                    table.CheckConstraint("ck_usage_operation_fence", "\"Fence\" > 0");
                    table.CheckConstraint("ck_usage_operation_hashes", "octet_length(\"IdempotencyHash\") = 32 AND octet_length(\"OwnerToken\") = 32 AND octet_length(\"PolicyFingerprint\") = 32 AND \"PolicyRevision\" > 0 AND (\"SettlementFingerprint\" IS NULL OR octet_length(\"SettlementFingerprint\") = 32)");
                    table.CheckConstraint("ck_usage_operation_kind", "\"Kind\" IN (0, 1, 2)");
                    table.CheckConstraint("ck_usage_operation_state", "\"State\" BETWEEN 0 AND 4");
                    table.CheckConstraint("ck_usage_operation_terminal", "(\"State\" IN (0, 1) AND \"SettledAt\" IS NULL AND \"SettlementFingerprint\" IS NULL) OR (\"State\" IN (2, 3, 4) AND \"SettledAt\" IS NOT NULL AND \"SettlementFingerprint\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_usage_operations_principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalSchema: "snowshot",
                        principalTable: "principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "provider_attempts",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptNumber = table.Column<int>(type: "integer", nullable: false),
                    Provider = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    DispatchState = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HttpStatus = table.Column<int>(type: "integer", nullable: true),
                    InputUnits = table.Column<long>(type: "bigint", nullable: false),
                    OutputUnits = table.Column<long>(type: "bigint", nullable: false),
                    CostNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    CostKnown = table.Column<bool>(type: "boolean", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_attempts", x => x.Id);
                    table.CheckConstraint("ck_provider_attempt_lifecycle", "(\"State\" = 0 AND \"DispatchState\" = 0 AND \"CompletedAt\" IS NULL AND \"Outcome\" IS NULL AND \"HttpStatus\" IS NULL AND \"InputUnits\" = 0 AND \"OutputUnits\" = 0 AND \"CostNanoYuan\" = 0 AND NOT \"CostKnown\") OR (\"State\" = 1 AND \"DispatchState\" IN (1, 2, 3) AND \"CompletedAt\" IS NOT NULL AND \"CompletedAt\" >= \"StartedAt\" AND \"Outcome\" IS NOT NULL AND (\"DispatchState\" <> 1 OR (\"CostKnown\" AND \"CostNanoYuan\" = 0)))");
                    table.CheckConstraint("ck_provider_attempt_state", "\"State\" IN (0, 1) AND \"DispatchState\" BETWEEN 0 AND 3");
                    table.CheckConstraint("ck_provider_attempt_values", "\"AttemptNumber\" > 0 AND \"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"CostNanoYuan\" >= 0 AND (\"CostKnown\" OR \"CostNanoYuan\" = 0)");
                    table.ForeignKey(
                        name: "FK_provider_attempts_usage_operations_OperationId",
                        column: x => x.OperationId,
                        principalSchema: "snowshot",
                        principalTable: "usage_operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "usage_events",
                schema: "snowshot",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputUnits = table.Column<long>(type: "bigint", nullable: false),
                    OutputUnits = table.Column<long>(type: "bigint", nullable: false),
                    PublicCostNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OperatorCostNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    OperatorOverageNanoYuan = table.Column<long>(type: "bigint", nullable: false),
                    CostKnown = table.Column<bool>(type: "boolean", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_usage_events", x => x.Id);
                    table.CheckConstraint("ck_usage_event_values", "\"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"PublicCostNanoYuan\" >= 0 AND \"OperatorCostNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0");
                    table.ForeignKey(
                        name: "FK_usage_events_usage_operations_OperationId",
                        column: x => x.OperationId,
                        principalSchema: "snowshot",
                        principalTable: "usage_operations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_allowance_periods_retention",
                schema: "snowshot",
                table: "allowance_periods",
                column: "PeriodDate");

            migrationBuilder.CreateIndex(
                name: "ix_daily_aggregates_retention",
                schema: "snowshot",
                table: "daily_aggregates",
                column: "UsageDate");

            migrationBuilder.CreateIndex(
                name: "ix_operator_budget_periods_retention",
                schema: "snowshot",
                table: "operator_budget_periods",
                column: "UpdatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_policy_revisions_fingerprint",
                schema: "snowshot",
                table: "policy_revisions",
                column: "Fingerprint");

            migrationBuilder.CreateIndex(
                name: "IX_policy_state_ActiveRevision",
                schema: "snowshot",
                table: "policy_state",
                column: "ActiveRevision");

            migrationBuilder.CreateIndex(
                name: "ix_principal_fingerprints_principal",
                schema: "snowshot",
                table: "principal_fingerprints",
                column: "PrincipalId");

            migrationBuilder.CreateIndex(
                name: "ix_principal_fingerprints_retention",
                schema: "snowshot",
                table: "principal_fingerprints",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "ix_principals_retention",
                schema: "snowshot",
                table: "principals",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "ix_provider_attempts_retention",
                schema: "snowshot",
                table: "provider_attempts",
                column: "CompletedAt");

            migrationBuilder.CreateIndex(
                name: "uq_provider_attempt_operation_number",
                schema: "snowshot",
                table: "provider_attempts",
                columns: new[] { "OperationId", "AttemptNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_events_retention",
                schema: "snowshot",
                table: "usage_events",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "uq_usage_events_operation",
                schema: "snowshot",
                table: "usage_events",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_principal_period",
                schema: "snowshot",
                table: "usage_operations",
                columns: new[] { "PrincipalId", "AllowanceDate" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_reconciliation",
                schema: "snowshot",
                table: "usage_operations",
                columns: new[] { "State", "LeaseExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "ix_usage_operations_retention",
                schema: "snowshot",
                table: "usage_operations",
                columns: new[] { "State", "SettledAt" });

            migrationBuilder.CreateIndex(
                name: "uq_usage_operations_idempotency_hash",
                schema: "snowshot",
                table: "usage_operations",
                column: "IdempotencyHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allowance_periods",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "daily_aggregates",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "operator_budget_periods",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "policy_state",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "principal_fingerprints",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "provider_attempts",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "usage_events",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "policy_revisions",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "usage_operations",
                schema: "snowshot");

            migrationBuilder.DropTable(
                name: "principals",
                schema: "snowshot");
        }
    }
}
