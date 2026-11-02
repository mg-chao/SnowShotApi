using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1861:Avoid constant arrays as arguments", Justification = "EF-generated one-time schema migration uses declarative index column arrays.")]
    public partial class AddCheckoutRecoveryAndEventRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RoutingLastAttemptAt",
                schema: "snowshot",
                table: "account_platform_events",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MerchantOrderId",
                schema: "snowshot",
                table: "account_platform_events",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "RoutingMetadataComplete",
                schema: "snowshot",
                table: "account_platform_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "CheckoutAttemptId",
                schema: "snowshot",
                table: "account_payment_orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_platform_events_MerchantOrderId",
                schema: "snowshot",
                table: "account_platform_events",
                column: "MerchantOrderId",
                filter: "\"EventName\" = 'xpay_refund_notify' AND \"State\" <> 'processed'");

            migrationBuilder.CreateIndex(
                name: "ix_account_platform_events_unrouted_refunds",
                schema: "snowshot",
                table: "account_platform_events",
                columns: new[] { "RoutingLastAttemptAt", "Id" },
                filter: "\"EventName\" = 'xpay_refund_notify' AND \"State\" <> 'processed' AND NOT \"RoutingMetadataComplete\"")
                .Annotation("Npgsql:IndexNullSortOrder", new[] { NullSortOrder.NullsFirst, NullSortOrder.NullsLast });

            migrationBuilder.CreateIndex(
                name: "IX_account_payment_orders_CheckoutAttemptId",
                schema: "snowshot",
                table: "account_payment_orders",
                column: "CheckoutAttemptId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_account_platform_events_MerchantOrderId",
                schema: "snowshot",
                table: "account_platform_events");

            migrationBuilder.DropIndex(
                name: "ix_account_platform_events_unrouted_refunds",
                schema: "snowshot",
                table: "account_platform_events");

            migrationBuilder.DropIndex(
                name: "IX_account_payment_orders_CheckoutAttemptId",
                schema: "snowshot",
                table: "account_payment_orders");

            migrationBuilder.DropColumn(
                name: "RoutingLastAttemptAt",
                schema: "snowshot",
                table: "account_platform_events");

            migrationBuilder.DropColumn(
                name: "MerchantOrderId",
                schema: "snowshot",
                table: "account_platform_events");

            migrationBuilder.DropColumn(
                name: "RoutingMetadataComplete",
                schema: "snowshot",
                table: "account_platform_events");

            migrationBuilder.DropColumn(
                name: "CheckoutAttemptId",
                schema: "snowshot",
                table: "account_payment_orders");
        }
    }
}
