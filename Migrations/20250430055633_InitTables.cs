using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class InitTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpUsers",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AssoId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    AssoId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTranslationOrders",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ContentLength = table.Column<int>(type: "integer", nullable: false),
                    ContentByteCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTranslationOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserTranslationUserOrderStats",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    Date = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    ContentLengthSum = table.Column<int>(type: "integer", nullable: false),
                    ContentByteCountSum = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false, defaultValueSql: "CURRENT_TIMESTAMP")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserTranslationUserOrderStats", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IpUsers_IpAddress",
                table: "IpUsers",
                column: "IpAddress");

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

            migrationBuilder.CreateIndex(
                name: "IX_Users_Type_AssoId",
                table: "Users",
                columns: new[] { "Type", "AssoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrders_CreatedAt",
                table: "UserTranslationOrders",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_UserTranslationOrders_Type",
                table: "UserTranslationOrders",
                column: "Type");

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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IpUsers");

            migrationBuilder.DropTable(
                name: "UserOrders");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropTable(
                name: "UserTranslationOrders");

            migrationBuilder.DropTable(
                name: "UserTranslationUserOrderStats");
        }
    }
}
