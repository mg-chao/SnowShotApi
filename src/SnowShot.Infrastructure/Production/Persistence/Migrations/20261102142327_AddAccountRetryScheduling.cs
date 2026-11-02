using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "EF-generated one-time schema migration uses declarative index column arrays.")]
    public partial class AddAccountRetryScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextQueryAt",
                schema: "snowshot",
                table: "account_refunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QueryAttempts",
                schema: "snowshot",
                table: "account_refunds",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "QueryLeaseExpiresAt",
                schema: "snowshot",
                table: "account_refunds",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "QueryLeaseToken",
                schema: "snowshot",
                table: "account_refunds",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DeliveryAttempts",
                schema: "snowshot",
                table: "account_payment_orders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeliveryLeaseExpiresAt",
                schema: "snowshot",
                table: "account_payment_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeliveryLeaseToken",
                schema: "snowshot",
                table: "account_payment_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextDeliveryAttemptAt",
                schema: "snowshot",
                table: "account_payment_orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_refunds_NextQueryAt_UpdatedAt_Id",
                schema: "snowshot",
                table: "account_refunds",
                columns: new[] { "NextQueryAt", "UpdatedAt", "Id" },
                filter: "\"State\" = 'processing'");

            migrationBuilder.CreateIndex(
                name: "IX_account_payment_orders_NextDeliveryAttemptAt_CreatedAt_Id",
                schema: "snowshot",
                table: "account_payment_orders",
                columns: new[] { "NextDeliveryAttemptAt", "CreatedAt", "Id" },
                filter: "\"Fulfilled\" AND NOT \"DeliveryConfirmed\"");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_account_refunds_NextQueryAt_UpdatedAt_Id",
                schema: "snowshot",
                table: "account_refunds");

            migrationBuilder.DropIndex(
                name: "IX_account_payment_orders_NextDeliveryAttemptAt_CreatedAt_Id",
                schema: "snowshot",
                table: "account_payment_orders");

            migrationBuilder.DropColumn(
                name: "NextQueryAt",
                schema: "snowshot",
                table: "account_refunds");

            migrationBuilder.DropColumn(
                name: "QueryAttempts",
                schema: "snowshot",
                table: "account_refunds");

            migrationBuilder.DropColumn(
                name: "QueryLeaseExpiresAt",
                schema: "snowshot",
                table: "account_refunds");

            migrationBuilder.DropColumn(
                name: "QueryLeaseToken",
                schema: "snowshot",
                table: "account_refunds");

            migrationBuilder.DropColumn(
                name: "DeliveryAttempts",
                schema: "snowshot",
                table: "account_payment_orders");

            migrationBuilder.DropColumn(
                name: "DeliveryLeaseExpiresAt",
                schema: "snowshot",
                table: "account_payment_orders");

            migrationBuilder.DropColumn(
                name: "DeliveryLeaseToken",
                schema: "snowshot",
                table: "account_payment_orders");

            migrationBuilder.DropColumn(
                name: "NextDeliveryAttemptAt",
                schema: "snowshot",
                table: "account_payment_orders");
        }
    }
}
