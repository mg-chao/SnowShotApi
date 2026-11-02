using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCumulativeMembership : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_wallet_nonnegative",
                schema: "snowshot",
                table: "account_wallets");

            migrationBuilder.AddColumn<bool>(
                name: "MembershipRecognitionFree",
                schema: "snowshot",
                table: "usage_operations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "CumulativeRechargeNanoYuan",
                schema: "snowshot",
                table: "account_wallets",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            // Aggregate payments and refunds independently to avoid multiplying a
            // payment by its number of refunds. Pending/conflicting orders and
            // refunds retained before fulfillment have no wallet effect yet.
            migrationBuilder.Sql("""
                UPDATE snowshot.account_wallets AS wallet
                SET "CumulativeRechargeNanoYuan" = (
                    COALESCE((SELECT SUM(payment."PaidFen"::bigint)
                        FROM snowshot.account_payment_orders AS payment
                        WHERE payment."AccountId" = wallet."AccountId" AND payment."Fulfilled"), 0)
                    - COALESCE((SELECT SUM(refund."RefundFen"::bigint)
                        FROM snowshot.account_refunds AS refund
                        JOIN snowshot.account_payment_orders AS payment ON payment."Id" = refund."OrderId"
                        WHERE payment."AccountId" = wallet."AccountId" AND payment."Fulfilled"
                            AND refund."Applied" AND refund."State" = 'completed'), 0)
                    ) * 10000000;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_wallet_nonnegative",
                schema: "snowshot",
                table: "account_wallets",
                sql: "\"AvailableNanoYuan\" >= 0 AND \"HeldNanoYuan\" >= 0 AND \"OutstandingNanoYuan\" >= 0 AND \"CumulativeRechargeNanoYuan\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_account_wallet_nonnegative",
                schema: "snowshot",
                table: "account_wallets");

            migrationBuilder.DropColumn(
                name: "MembershipRecognitionFree",
                schema: "snowshot",
                table: "usage_operations");

            migrationBuilder.DropColumn(
                name: "CumulativeRechargeNanoYuan",
                schema: "snowshot",
                table: "account_wallets");

            migrationBuilder.AddCheckConstraint(
                name: "ck_account_wallet_nonnegative",
                schema: "snowshot",
                table: "account_wallets",
                sql: "\"AvailableNanoYuan\" >= 0 AND \"HeldNanoYuan\" >= 0 AND \"OutstandingNanoYuan\" >= 0");
        }
    }
}
