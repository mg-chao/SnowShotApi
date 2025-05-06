using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslateTokensField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CompletionTokens",
                table: "UserTranslationOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "PromptTokens",
                table: "UserTranslationOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "UserTranslationOrders");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "UserTranslationOrders");
        }
    }
}
