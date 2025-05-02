using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslateFromToField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ContentByteCountSum",
                table: "UserTranslationUserOrderStats");

            migrationBuilder.DropColumn(
                name: "ContentByteCount",
                table: "UserTranslationOrders");

            migrationBuilder.AddColumn<string>(
                name: "From",
                table: "UserTranslationOrders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "To",
                table: "UserTranslationOrders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "From",
                table: "UserTranslationOrders");

            migrationBuilder.DropColumn(
                name: "To",
                table: "UserTranslationOrders");

            migrationBuilder.AddColumn<int>(
                name: "ContentByteCountSum",
                table: "UserTranslationUserOrderStats",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "ContentByteCount",
                table: "UserTranslationOrders",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }
    }
}
