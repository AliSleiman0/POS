using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class Modifiers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_modifier",
                table: "product",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "modifier_group",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    min_selections = table.Column<int>(type: "integer", nullable: false),
                    max_selections = table.Column<int>(type: "integer", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modifier_group", x => x.id);
                    table.UniqueConstraint("ak_modifier_group_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_modifier_group_name_length", "length(\"name\") <= 80");
                    table.CheckConstraint("ck_modifier_group_selection_range", "min_selections >= 0 AND (max_selections IS NULL OR max_selections >= min_selections)");
                });

            migrationBuilder.CreateTable(
                name: "modifier_option",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    modifier_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_modifier_option", x => x.id);
                    table.ForeignKey(
                        name: "fk_modifier_option_group",
                        columns: x => new { x.tenant_id, x.modifier_group_id },
                        principalTable: "modifier_group",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_modifier_option_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "product_modifier_group",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    modifier_group_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product_modifier_group", x => x.id);
                    table.ForeignKey(
                        name: "fk_product_modifier_group_group",
                        columns: x => new { x.tenant_id, x.modifier_group_id },
                        principalTable: "modifier_group",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_modifier_group_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_modifier_group_tenant_sort",
                table: "modifier_group",
                columns: new[] { "tenant_id", "sort_order", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_modifier_option_tenant_id_product_id",
                table: "modifier_option",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_modifier_option_tenant_group_product",
                table: "modifier_option",
                columns: new[] { "tenant_id", "modifier_group_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_product_modifier_group_tenant_id_modifier_group_id",
                table: "product_modifier_group",
                columns: new[] { "tenant_id", "modifier_group_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_modifier_group_tenant_product_sort",
                table: "product_modifier_group",
                columns: new[] { "tenant_id", "product_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_product_modifier_group_tenant_product_group",
                table: "product_modifier_group",
                columns: new[] { "tenant_id", "product_id", "modifier_group_id" },
                unique: true);

            // Three new tenant-owned tables, so the catalog-driven loop has to run again. A
            // migration that has already been applied does not re-run, so tables introduced
            // here are covered only if this is called here.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "modifier_option");

            migrationBuilder.DropTable(
                name: "product_modifier_group");

            migrationBuilder.DropTable(
                name: "modifier_group");

            migrationBuilder.DropColumn(
                name: "is_modifier",
                table: "product");

            // Apply, not Remove: Remove is catalog-driven and would strip every tenant table
            // rather than the three this migration created.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
