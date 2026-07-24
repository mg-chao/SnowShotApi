using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using SnowShotApi.Data;

#nullable disable

namespace SnowShotApi.Migrations;

[DbContext(typeof(ApplicationDbContext))]
[Migration("20260724090000_AddTranslationOrderQuotaDate")]
public partial class AddTranslationOrderQuotaDate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "QuotaDate",
            table: "UserTranslationOrders",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.Sql("""
            UPDATE "UserTranslationOrders"
            SET "QuotaDate" = TO_CHAR(
                "CreatedAt" AT TIME ZONE 'Asia/Shanghai',
                'YYYYMMDD')::integer;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "QuotaDate",
            table: "UserTranslationOrders");
    }
}
