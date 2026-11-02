using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAccountDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "snowshot",
                table: "accounts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeletionConsentVersion",
                schema: "snowshot",
                table: "accounts",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ForfeitedCreditNanoYuan",
                schema: "snowshot",
                table: "accounts",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "snowshot",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "DeletionConsentVersion",
                schema: "snowshot",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "ForfeitedCreditNanoYuan",
                schema: "snowshot",
                table: "accounts");
        }
    }
}
