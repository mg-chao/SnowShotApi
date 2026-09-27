using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLatexExtraction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_kind",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_kind",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"Kind\" IN (0, 1, 2, 3)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_kind",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_kind",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"Kind\" IN (0, 1, 2)");
        }
    }
}
