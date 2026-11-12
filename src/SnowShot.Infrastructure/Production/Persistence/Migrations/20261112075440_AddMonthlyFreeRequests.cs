using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMonthlyFreeRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AllowanceFunding",
                schema: "snowshot",
                table: "usage_operations",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "monthly_free_request_periods",
                schema: "snowshot",
                columns: table => new
                {
                    PrincipalId = table.Column<Guid>(type: "uuid", nullable: false),
                    Resource = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MonthStart = table.Column<DateOnly>(type: "date", nullable: false),
                    CommittedRequests = table.Column<int>(type: "integer", nullable: false),
                    ReservedRequests = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_monthly_free_request_periods", x => new { x.PrincipalId, x.Resource, x.MonthStart });
                    table.CheckConstraint("ck_monthly_free_requests_month_start", "EXTRACT(DAY FROM \"MonthStart\") = 1");
                    table.CheckConstraint("ck_monthly_free_requests_nonnegative", "\"CommittedRequests\" >= 0 AND \"ReservedRequests\" >= 0");
                    table.ForeignKey(
                        name: "FK_monthly_free_request_periods_principals_PrincipalId",
                        column: x => x.PrincipalId,
                        principalSchema: "snowshot",
                        principalTable: "principals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_usage_operation_allowance_funding",
                schema: "snowshot",
                table: "usage_operations",
                sql: "\"AllowanceFunding\" IN (0, 1, 2) AND (\"AllowanceFunding\" <> 2 OR (\"ReservedPublicNanoYuan\" = 0 AND \"ReservedFreeNanoYuan\" = 0 AND \"ReservedWalletNanoYuan\" = 0 AND NOT \"MembershipRecognitionFree\"))");

            migrationBuilder.CreateIndex(
                name: "ix_monthly_free_requests_retention",
                schema: "snowshot",
                table: "monthly_free_request_periods",
                column: "MonthStart");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "monthly_free_request_periods",
                schema: "snowshot");

            migrationBuilder.DropCheckConstraint(
                name: "ck_usage_operation_allowance_funding",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "AllowanceFunding",
                schema: "snowshot",
                table: "usage_operations");
        }
    }
}
