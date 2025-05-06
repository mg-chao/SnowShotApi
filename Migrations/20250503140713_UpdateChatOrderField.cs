using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class UpdateChatOrderField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserTranslationUserOrderStats");

            migrationBuilder.CreateTable(
                name: "UserChatOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Model = table.Column<string>(type: "text", nullable: false),
                    PromptTokens = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokens = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserChatOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserChatOrderStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: false),
                    PromptTokensSum = table.Column<int>(type: "integer", nullable: false),
                    CompletionTokensSum = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserChatOrderStats", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTranslationOrderStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ContentLengthSum = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTranslationOrderStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrders_CreatedAt",
                table: "UserChatOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrders_Model",
                table: "UserChatOrders",
                column: "Model");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrderStats_CreatedAt",
                table: "UserChatOrderStats",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserChatOrderStats_Date_Model",
                table: "UserChatOrderStats",
                columns: new[] { "Date", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrderStats_CreatedAt",
                table: "UserTranslationOrderStats",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrderStats_Date_Type",
                table: "UserTranslationOrderStats",
                columns: new[] { "Date", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrderStats_UserId",
                table: "UserTranslationOrderStats",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "UserChatOrders");

            migrationBuilder.DropTable(
                name: "UserChatOrderStats");

            migrationBuilder.DropTable(
                name: "UserTranslationOrderStats");

            migrationBuilder.CreateTable(
                name: "UserTranslationUserOrderStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ContentLengthSum = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP"),
                    Date = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTranslationUserOrderStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationUserOrderStats_CreatedAt",
                table: "UserTranslationUserOrderStats",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationUserOrderStats_Date_Type",
                table: "UserTranslationUserOrderStats",
                columns: new[] { "Date", "Type" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationUserOrderStats_UserId",
                table: "UserTranslationUserOrderStats",
                column: "UserId");
        }
    }
}
