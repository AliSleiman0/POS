using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// The catalog and inventory tables: category, tax class, product, barcode, stock item.
    /// </summary>
    /// <remarks>
    /// Two things here are deliberate and will look like mistakes to a reader.
    /// <para>
    /// <b>Every foreign key carries <c>tenant_id</c>.</b> Postgres exempts referential
    /// integrity checks from row-level security, so a single-column key on <c>product_id</c>
    /// would let one tenant reference another tenant's product and the check would accept
    /// it. The composite key makes that a constraint violation instead. The alternate keys
    /// (<c>ak_*_tenant_id_id</c>) exist to be pointed at.
    /// </para>
    /// <para>
    /// <b><c>stock_item</c> has an <c>xmin</c> column in the scaffolded C# above and none in
    /// the SQL this produces.</b> That is correct: <c>xmin</c> is a Postgres system column
    /// that every row already has, and Npgsql's SQL generator suppresses migration
    /// operations against it. It is mapped as the optimistic concurrency token — see
    /// StockItemConfiguration. Do not "fix" the C# by deleting the column: the property has
    /// to be in the model for the mapping to exist.
    /// </para>
    /// </remarks>
    public partial class CatalogAndInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "category",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    parent_category_id = table.Column<Guid>(type: "uuid", nullable: true),
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
                    table.PrimaryKey("pk_category", x => x.id);
                    table.UniqueConstraint("ak_category_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_category_name_length", "length(\"name\") <= 100");
                    table.ForeignKey(
                        name: "fk_category_parent",
                        columns: x => new { x.tenant_id, x.parent_category_id },
                        principalTable: "category",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tax_class",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    rate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: false),
                    is_default = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tax_class", x => x.id);
                    table.UniqueConstraint("ak_tax_class_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_tax_class_name_length", "length(\"name\") <= 60");
                    table.CheckConstraint("ck_tax_class_rate_range", "rate >= 0 AND rate <= 1");
                });

            migrationBuilder.CreateTable(
                name: "product",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sku = table.Column<string>(type: "text", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    description = table.Column<string>(type: "text", nullable: true),
                    category_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tax_class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    cost_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    unit = table.Column<string>(type: "text", nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    track_stock = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_product", x => x.id);
                    table.UniqueConstraint("ak_product_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_product_description_length", "length(\"description\") <= 1000");
                    table.CheckConstraint("ck_product_name_length", "length(\"name\") <= 200");
                    table.CheckConstraint("ck_product_sku_length", "length(\"sku\") <= 64");
                    table.CheckConstraint("ck_product_unit_allowed", "\"unit\" IN ('Each', 'Kilogram', 'Litre')");
                    table.ForeignKey(
                        name: "fk_product_category",
                        columns: x => new { x.tenant_id, x.category_id },
                        principalTable: "category",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_product_tax_class",
                        columns: x => new { x.tenant_id, x.tax_class_id },
                        principalTable: "tax_class",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "barcode",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    code = table.Column<string>(type: "text", nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_barcode", x => x.id);
                    table.CheckConstraint("ck_barcode_code_length", "length(\"code\") <= 64");
                    table.ForeignKey(
                        name: "fk_barcode_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_item",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    on_hand = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reorder_point = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_item", x => x.id);
                    table.ForeignKey(
                        name: "fk_stock_item_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_barcode_tenant_product",
                table: "barcode",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ux_barcode_tenant_code",
                table: "barcode",
                columns: new[] { "tenant_id", "code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_category_tenant_parent",
                table: "category",
                columns: new[] { "tenant_id", "parent_category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_tenant_category",
                table: "product",
                columns: new[] { "tenant_id", "category_id" });

            migrationBuilder.CreateIndex(
                name: "ix_product_tenant_name",
                table: "product",
                columns: new[] { "tenant_id", "name" });

            migrationBuilder.CreateIndex(
                name: "ix_product_tenant_tax_class",
                table: "product",
                columns: new[] { "tenant_id", "tax_class_id" });

            migrationBuilder.CreateIndex(
                name: "ux_product_tenant_sku",
                table: "product",
                columns: new[] { "tenant_id", "sku" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_stock_item_tenant_product",
                table: "stock_item",
                columns: new[] { "tenant_id", "product_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_tax_class_tenant_default",
                table: "tax_class",
                column: "tenant_id",
                unique: true,
                filter: "is_default");

            // Five new tables carrying tenant_id, and an applied migration does not re-run —
            // so the RowLevelSecurity migration cannot cover them and this one must.
            // RowLevelSecurityTests.Every_tenant_owned_table_is_covered fails without it.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "barcode");

            migrationBuilder.DropTable(
                name: "stock_item");

            migrationBuilder.DropTable(
                name: "product");

            migrationBuilder.DropTable(
                name: "category");

            migrationBuilder.DropTable(
                name: "tax_class");

            // Apply, NOT Remove — and this is the opposite of what the extension's own
            // remarks suggest, so it needs saying. RemoveTenantRowLevelSecurity() loops over
            // every table with a tenant_id column, so calling it here would strip the
            // policies from register, application_user, refresh_token and the Identity join
            // tables as well, and leave them stripped: the RowLevelSecurity migration that
            // created them has already been applied and never re-runs. Re-applying after
            // the drops restores the correct end state instead, and the loop is idempotent.
            // The convention as documented is right for the first RLS migration and wrong
            // for every one after it.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
