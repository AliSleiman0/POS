using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class TenantPrimitives : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "tenant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    slug = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    currency_code = table.Column<string>(type: "text", nullable: false),
                    time_zone_id = table.Column<string>(type: "text", nullable: false),
                    tax_mode = table.Column<string>(type: "text", nullable: false),
                    business_day_start_offset = table.Column<TimeSpan>(type: "interval", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tenant", x => x.id);
                    table.CheckConstraint("ck_tenant_currency_code_length", "length(\"currency_code\") <= 3");
                    table.CheckConstraint("ck_tenant_name_length", "length(\"name\") <= 200");
                    table.CheckConstraint("ck_tenant_slug_format", "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                    table.CheckConstraint("ck_tenant_slug_length", "length(\"slug\") <= 63");
                    table.CheckConstraint("ck_tenant_tax_mode_allowed", "\"tax_mode\" IN ('Inclusive', 'Exclusive')");
                    table.CheckConstraint("ck_tenant_time_zone_id_length", "length(\"time_zone_id\") <= 64");
                });

            migrationBuilder.CreateIndex(
                name: "ux_tenant_slug",
                table: "tenant",
                column: "slug",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "tenant");
        }
    }
}
