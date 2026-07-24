using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class FixUserChatOrderStatsDailyUniqueIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserChatOrderStats_Date_Model",
                table: "UserChatOrderStats");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrderStats_UserId_Date_Model",
                table: "UserChatOrderStats",
                columns: new[] { "UserId", "Date", "Model" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserChatOrderStats_UserId_Date_Model",
                table: "UserChatOrderStats");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrderStats_Date_Model",
                table: "UserChatOrderStats",
                columns: new[] { "Date", "Model" },
                unique: true);
        }
    }
}
