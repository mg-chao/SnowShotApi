using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShot.Infrastructure.Production.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddTemporaryWechatAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsTemporary",
                schema: "snowshot",
                table: "accounts",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "CompletionKind",
                schema: "snowshot",
                table: "account_purchase_requests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PayerOpenId",
                schema: "snowshot",
                table: "account_purchase_requests",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_account_purchase_requests_PayerOpenId_State",
                schema: "snowshot",
                table: "account_purchase_requests",
                columns: ["PayerOpenId", "State"]);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_account_purchase_requests_PayerOpenId_State",
                schema: "snowshot",
                table: "account_purchase_requests");

            migrationBuilder.DropColumn(
                name: "IsTemporary",
                schema: "snowshot",
                table: "accounts");

            migrationBuilder.DropColumn(
                name: "CompletionKind",
                schema: "snowshot",
                table: "account_purchase_requests");

            migrationBuilder.DropColumn(
                name: "PayerOpenId",
                schema: "snowshot",
                table: "account_purchase_requests");
        }
    }
}
