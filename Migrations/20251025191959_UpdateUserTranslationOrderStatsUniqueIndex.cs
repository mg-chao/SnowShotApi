using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class UpdateUserTranslationOrderStatsUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserTranslationOrderStats_Date_Type",
                table: "UserTranslationOrderStats");

            migrationBuilder.DropColumn(
                name: "CompletionTokens",
                table: "UserTranslationOrders");

            migrationBuilder.DropColumn(
                name: "PromptTokens",
                table: "UserTranslationOrders");

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrderStats_UserId_Date_Type",
                table: "UserTranslationOrderStats",
                columns: new[] { "UserId", "Date", "Type" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserTranslationOrderStats_UserId_Date_Type",
                table: "UserTranslationOrderStats");

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

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrderStats_Date_Type",
                table: "UserTranslationOrderStats",
                columns: new[] { "Date", "Type" },
                unique: true);
        }
    }
}
