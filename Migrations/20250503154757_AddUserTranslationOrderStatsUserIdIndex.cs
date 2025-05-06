using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTranslationOrderStatsUserIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserOrders");

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "UserTranslationOrders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<long>(
                name: "UserId",
                table: "UserChatOrders",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrders_UserId",
                table: "UserTranslationOrders",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrderStats_UserId",
                table: "UserChatOrderStats",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrders_UserId",
                table: "UserChatOrders",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_UserTranslationOrders_UserId",
                table: "UserTranslationOrders");

            migrationBuilder.DropIndex(
                name: "IX_UserChatOrderStats_UserId",
                table: "UserChatOrderStats");

            migrationBuilder.DropIndex(
                name: "IX_UserChatOrders_UserId",
                table: "UserChatOrders");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "UserTranslationOrders");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "UserChatOrders");

            migrationBuilder.CreateTable(
                name: "UserOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AssoId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOrders", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserOrders_CreatedAt",
                table: "UserOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserOrders_Type_AssoId",
                table: "UserOrders",
                columns: new[] { "Type", "AssoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserOrders_UserId",
                table: "UserOrders",
                column: "UserId");
        }
    }
}
