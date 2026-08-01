using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// <c>stock_movement</c>: the append-only ledger <c>stock_item.on_hand</c> is a cache of.
    /// </summary>
    /// <remarks>
    /// The table has no <c>UPDATE</c> or <c>DELETE</c> constraint stopping a rewrite, and
    /// that is not an omission — append-only is enforced by the code that writes it and by
    /// review, the same way <c>Sale</c> will be in Phase 3. A trigger refusing updates would
    /// also refuse the corrective migration nobody has needed yet, and would have to be
    /// dropped by the person doing it, which is exactly when the protection is worth least.
    /// <para>
    /// <c>quantity</c> is <c>numeric(19,4)</c> and <b>signed</b>: positive into the shop,
    /// negative out of it. There is deliberately no <c>quantity &lt;&gt; 0</c> check —
    /// the sign rules are per movement type and live in <c>StockRules</c>, where they can say
    /// why.
    /// </para>
    /// </remarks>
    public partial class StockLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "stock_movement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: true),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_movement", x => x.id);
                    table.CheckConstraint("ck_stock_movement_reason_length", "length(\"reason\") <= 200");
                    table.CheckConstraint("ck_stock_movement_type_allowed", "\"type\" IN ('Receive', 'Adjust', 'Sale', 'Refund', 'Waste', 'Recount')");
                    table.ForeignKey(
                        name: "fk_stock_movement_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_tenant_product_occurred",
                table: "stock_movement",
                columns: new[] { "tenant_id", "product_id", "occurred_at" });

            // A new table carrying tenant_id, and an applied migration does not re-run — so
            // the RowLevelSecurity migration cannot cover it and this one must.
            // RowLevelSecurityTests.Every_tenant_owned_table_is_covered fails without it.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "stock_movement");

            // Apply, NOT Remove. RemoveTenantRowLevelSecurity() loops over every table with a
            // tenant_id column, so calling it here would strip the policies from the catalog,
            // register and Identity tables too — and leave them stripped, because the
            // migrations that created them have already been applied and never re-run.
            // Re-applying after the drop restores the correct end state, and the loop is
            // idempotent. Same reasoning as CatalogAndInventory, which says it at length.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
