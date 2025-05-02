using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SnowShotApi.Migrations
{
    /// <inheritdoc />
    public partial class AddTranslateDomainField : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Domain",
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
                name: "Domain",
                table: "UserTranslationOrders");
        }
    }
}
