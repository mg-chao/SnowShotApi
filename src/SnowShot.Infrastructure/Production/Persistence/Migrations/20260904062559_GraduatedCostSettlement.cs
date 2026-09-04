using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class GraduatedCostSettlement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_state",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_terminal",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_event_values",
                schema: "snowshot",
                table: "usage_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_attempt_lifecycle",
                schema: "snowshot",
                table: "provider_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_attempt_values",
                schema: "snowshot",
                table: "provider_attempts");

            migrationBuilder.AddColumn<int>(
                name: "CostBasis",
                schema: "snowshot",
                table: "usage_events",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "CostBasis",
                schema: "snowshot",
                table: "provider_attempts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Historical rows settle as Exact where the cost was known and as Unknown
            // otherwise; no pre-existing row can carry an Estimated basis.
            migrationBuilder.Sql("""
                UPDATE snowshot.usage_events SET "CostBasis" = CASE WHEN "CostKnown" THEN 0 ELSE 2 END;
                UPDATE snowshot.provider_attempts SET "CostBasis" = CASE WHEN "CostKnown" THEN 0 ELSE 2 END;
                """);

            migrationBuilder.DropColumn(
                name: "CostKnown",
                schema: "snowshot",
                table: "usage_events");

            migrationBuilder.DropColumn(
                name: "CostKnown",
                schema: "snowshot",
                table: "provider_attempts");

            migrationBuilder.AddColumn<long>(
                name: "EstimatedCostRequests",
                schema: "snowshot",
                table: "daily_aggregates",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_state",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"State\" BETWEEN 0 AND 5");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_terminal",
                schema: "snowshot",
                table: "usage_operations",
                sql: "(\"State\" IN (0, 1) AND \"SettledAt\" IS NULL AND \"SettlementFingerprint\" IS NULL) OR (\"State\" IN (2, 3, 4, 5) AND \"SettledAt\" IS NOT NULL AND \"SettlementFingerprint\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_event_values",
                schema: "snowshot",
                table: "usage_events",
                sql: "\"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"PublicCostNanoYuan\" >= 0 AND \"OperatorCostNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0 AND \"CostBasis\" BETWEEN 0 AND 2");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_attempt_lifecycle",
                schema: "snowshot",
                table: "provider_attempts",
                sql: "(\"State\" = 0 AND \"DispatchState\" = 0 AND \"CompletedAt\" IS NULL AND \"Outcome\" IS NULL AND \"HttpStatus\" IS NULL AND \"InputUnits\" = 0 AND \"OutputUnits\" = 0 AND \"CostNanoYuan\" = 0 AND \"CostBasis\" = 2) OR (\"State\" = 1 AND \"DispatchState\" IN (1, 2, 3) AND \"CompletedAt\" IS NOT NULL AND \"CompletedAt\" >= \"StartedAt\" AND \"Outcome\" IS NOT NULL AND (\"DispatchState\" <> 1 OR (\"CostBasis\" = 0 AND \"CostNanoYuan\" = 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_attempt_values",
                schema: "snowshot",
                table: "provider_attempts",
                sql: "\"AttemptNumber\" > 0 AND \"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"CostNanoYuan\" >= 0 AND \"CostBasis\" BETWEEN 0 AND 2 AND (\"CostBasis\" IN (0, 1) OR \"CostNanoYuan\" = 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_state",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_terminal",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_event_values",
                schema: "snowshot",
                table: "usage_events");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_attempt_lifecycle",
                schema: "snowshot",
                table: "provider_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_provider_attempt_values",
                schema: "snowshot",
                table: "provider_attempts");

            migrationBuilder.AddColumn<bool>(
                name: "CostKnown",
                schema: "snowshot",
                table: "usage_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CostKnown",
                schema: "snowshot",
                table: "provider_attempts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""
                UPDATE snowshot.usage_events SET "CostKnown" = ("CostBasis" = 0);
                UPDATE snowshot.provider_attempts SET "CostKnown" = ("CostBasis" = 0),
                    "CostNanoYuan" = CASE WHEN "CostBasis" = 1 THEN 0 ELSE "CostNanoYuan" END;
                UPDATE snowshot.usage_operations SET "State" = 4 WHERE "State" = 5;
                """);

            migrationBuilder.DropColumn(
                name: "CostBasis",
                schema: "snowshot",
                table: "usage_events");

            migrationBuilder.DropColumn(
                name: "CostBasis",
                schema: "snowshot",
                table: "provider_attempts");

            migrationBuilder.DropColumn(
                name: "EstimatedCostRequests",
                schema: "snowshot",
                table: "daily_aggregates");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_state",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"State\" BETWEEN 0 AND 4");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_terminal",
                schema: "snowshot",
                table: "usage_operations",
                sql: "(\"State\" IN (0, 1) AND \"SettledAt\" IS NULL AND \"SettlementFingerprint\" IS NULL) OR (\"State\" IN (2, 3, 4) AND \"SettledAt\" IS NOT NULL AND \"SettlementFingerprint\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_event_values",
                schema: "snowshot",
                table: "usage_events",
                sql: "\"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"PublicCostNanoYuan\" >= 0 AND \"OperatorCostNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_attempt_lifecycle",
                schema: "snowshot",
                table: "provider_attempts",
                sql: "(\"State\" = 0 AND \"DispatchState\" = 0 AND \"CompletedAt\" IS NULL AND \"Outcome\" IS NULL AND \"HttpStatus\" IS NULL AND \"InputUnits\" = 0 AND \"OutputUnits\" = 0 AND \"CostNanoYuan\" = 0 AND NOT \"CostKnown\") OR (\"State\" = 1 AND \"DispatchState\" IN (1, 2, 3) AND \"CompletedAt\" IS NOT NULL AND \"CompletedAt\" >= \"StartedAt\" AND \"Outcome\" IS NOT NULL AND (\"DispatchState\" <> 1 OR (\"CostKnown\" AND \"CostNanoYuan\" = 0)))");

            migrationBuilder.AddCheckConstraint(
                name: "ck_provider_attempt_values",
                schema: "snowshot",
                table: "provider_attempts",
                sql: "\"AttemptNumber\" > 0 AND \"InputUnits\" >= 0 AND \"OutputUnits\" >= 0 AND \"CostNanoYuan\" >= 0 AND (\"CostKnown\" OR \"CostNanoYuan\" = 0)");
        }
    }
}
