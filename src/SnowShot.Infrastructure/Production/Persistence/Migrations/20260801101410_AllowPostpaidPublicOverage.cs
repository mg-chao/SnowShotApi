using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowPostpaidPublicOverage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_costs",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_costs",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"InputRateNanoYuan\" >= 0 AND \"OutputRateNanoYuan\" >= 0 AND \"ReservedPublicNanoYuan\" >= 0 AND \"ReservedOperatorNanoYuan\" >= 0 AND \"ActualPublicNanoYuan\" >= 0 AND \"ActualOperatorNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_costs",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_costs",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"InputRateNanoYuan\" >= 0 AND \"OutputRateNanoYuan\" >= 0 AND \"ReservedPublicNanoYuan\" >= 0 AND \"ReservedOperatorNanoYuan\" >= 0 AND \"ActualPublicNanoYuan\" >= 0 AND \"ActualOperatorNanoYuan\" >= 0 AND \"OperatorOverageNanoYuan\" >= 0 AND \"ActualPublicNanoYuan\" <= \"ReservedPublicNanoYuan\"");
        }
    }
}
