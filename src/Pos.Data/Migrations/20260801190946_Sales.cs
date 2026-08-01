using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// The financial tables: <c>shift</c>, <c>sale</c>, <c>sale_line</c>, <c>tender</c>,
    /// <c>cash_movement</c>, <c>stock_discrepancy</c>, and the <c>sale_sequence</c> counter.
    /// </summary>
    /// <remarks>
    /// <c>shift</c> is created before <c>sale</c> because <c>sale.shift_id</c> is a composite
    /// foreign key into it. That is also why the shift tables land in this migration even
    /// though their endpoints are Phase 3.8 — a schema cannot be delivered in the order the
    /// milestones happen to be numbered.
    /// <para>
    /// <b>Append-only is enforced by the write path and by review, not by a trigger</b> — the
    /// same decision <c>StockLedger</c> recorded, for the same reason: a trigger refusing
    /// updates would also refuse the corrective migration nobody has needed yet, and would
    /// have to be dropped by the person doing it, which is when the protection is worth least.
    /// </para>
    /// <para>
    /// <c>register</c> gains <c>ak_register_tenant_id_id</c> here. Nothing pointed at a
    /// register until shifts and sales did, so it had no alternate key to be a principal for.
    /// </para>
    /// <para>
    /// <c>stock_movement.sale_id</c> finally gets its foreign key. It has been a bare
    /// <c>Guid?</c> since 2.4 because there was nothing to point at, and an unconstrained
    /// column is invisible to the model-walk test that checks every tenant-scoped relationship
    /// carries the tenant — so forgetting this would have failed nothing.
    /// </para>
    /// </remarks>
    public partial class Sales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddUniqueConstraint(
                name: "ak_register_tenant_id_id",
                table: "register",
                columns: new[] { "tenant_id", "id" });

            migrationBuilder.CreateTable(
                name: "sale_sequence",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    last_number = table.Column<long>(type: "bigint", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_sequence", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "shift",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    opening_float = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    counted_cash = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    expected_cash = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    variance = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_shift", x => x.id);
                    table.UniqueConstraint("ak_shift_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_shift_status_allowed", "\"status\" IN ('Open', 'Closed')");
                    table.ForeignKey(
                        name: "fk_shift_register",
                        columns: x => new { x.tenant_id, x.register_id },
                        principalTable: "register",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "cash_movement",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    reason = table.Column<string>(type: "text", nullable: false),
                    performed_by = table.Column<Guid>(type: "uuid", nullable: false),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_cash_movement", x => x.id);
                    table.CheckConstraint("ck_cash_movement_reason_length", "length(\"reason\") <= 200");
                    table.CheckConstraint("ck_cash_movement_type_allowed", "\"type\" IN ('Drop', 'Payout', 'PettyCash', 'Correction')");
                    table.ForeignKey(
                        name: "fk_cash_movement_shift",
                        columns: x => new { x.tenant_id, x.shift_id },
                        principalTable: "shift",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_number = table.Column<long>(type: "bigint", nullable: false),
                    client_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    shift_id = table.Column<Guid>(type: "uuid", nullable: false),
                    cashier_id = table.Column<Guid>(type: "uuid", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    tax_mode = table.Column<string>(type: "text", nullable: false),
                    original_sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    subtotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    rounding_adjustment = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "text", nullable: true),
                    refund_reason = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale", x => x.id);
                    table.UniqueConstraint("ak_sale_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_sale_refund_reason_length", "length(\"refund_reason\") <= 200");
                    table.CheckConstraint("ck_sale_status_allowed", "\"status\" IN ('Completed', 'Voided')");
                    table.CheckConstraint("ck_sale_tax_mode_allowed", "\"tax_mode\" IN ('Inclusive', 'Exclusive')");
                    table.CheckConstraint("ck_sale_type_allowed", "\"type\" IN ('Sale', 'Refund')");
                    table.CheckConstraint("ck_sale_void_reason_length", "length(\"void_reason\") <= 200");
                    table.ForeignKey(
                        name: "fk_sale_original",
                        columns: x => new { x.tenant_id, x.original_sale_id },
                        principalTable: "sale",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_register",
                        columns: x => new { x.tenant_id, x.register_id },
                        principalTable: "register",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_shift",
                        columns: x => new { x.tenant_id, x.shift_id },
                        principalTable: "shift",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sale_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    line_subtotal = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    line_tax = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    line_total = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    is_price_overridden = table.Column<bool>(type: "boolean", nullable: false),
                    overridden_by = table.Column<Guid>(type: "uuid", nullable: true),
                    original_sale_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sale_line", x => x.id);
                    table.UniqueConstraint("ak_sale_line_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_sale_line_description_length", "length(\"description\") <= 200");
                    table.ForeignKey(
                        name: "fk_sale_line_original",
                        columns: x => new { x.tenant_id, x.original_sale_line_id },
                        principalTable: "sale_line",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_line_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_sale_line_sale",
                        columns: x => new { x.tenant_id, x.sale_id },
                        principalTable: "sale",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "tender",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    method = table.Column<string>(type: "text", nullable: false),
                    amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    change_given = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: true),
                    reference = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_tender", x => x.id);
                    table.CheckConstraint("ck_tender_method_allowed", "\"method\" IN ('Cash', 'External', 'Card', 'Voucher')");
                    table.CheckConstraint("ck_tender_reference_length", "length(\"reference\") <= 100");
                    table.ForeignKey(
                        name: "fk_tender_sale",
                        columns: x => new { x.tenant_id, x.sale_id },
                        principalTable: "sale",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "stock_discrepancy",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity_requested = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    on_hand_after = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    detected_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_stock_discrepancy", x => x.id);
                    table.ForeignKey(
                        name: "fk_stock_discrepancy_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_discrepancy_sale",
                        columns: x => new { x.tenant_id, x.sale_id },
                        principalTable: "sale",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_stock_discrepancy_sale_line",
                        columns: x => new { x.tenant_id, x.sale_line_id },
                        principalTable: "sale_line",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_stock_movement_tenant_sale",
                table: "stock_movement",
                columns: new[] { "tenant_id", "sale_id" });

            migrationBuilder.CreateIndex(
                name: "ix_cash_movement_tenant_shift_occurred",
                table: "cash_movement",
                columns: new[] { "tenant_id", "shift_id", "occurred_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_tenant_completed_at",
                table: "sale",
                columns: new[] { "tenant_id", "completed_at" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_tenant_id_register_id",
                table: "sale",
                columns: new[] { "tenant_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_tenant_original",
                table: "sale",
                columns: new[] { "tenant_id", "original_sale_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_tenant_shift",
                table: "sale",
                columns: new[] { "tenant_id", "shift_id" });

            migrationBuilder.CreateIndex(
                name: "ux_sale_tenant_client_transaction_id",
                table: "sale",
                columns: new[] { "tenant_id", "client_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sale_tenant_sale_number",
                table: "sale",
                columns: new[] { "tenant_id", "sale_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_sale_line_tenant_id_product_id",
                table: "sale_line",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_line_tenant_original",
                table: "sale_line",
                columns: new[] { "tenant_id", "original_sale_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_sale_line_tenant_sale",
                table: "sale_line",
                columns: new[] { "tenant_id", "sale_id" });

            migrationBuilder.CreateIndex(
                name: "ux_sale_line_tenant_sale_line_number",
                table: "sale_line",
                columns: new[] { "tenant_id", "sale_id", "line_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_sale_sequence_tenant",
                table: "sale_sequence",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_shift_tenant_opened_at",
                table: "shift",
                columns: new[] { "tenant_id", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_shift_tenant_register_open",
                table: "shift",
                columns: new[] { "tenant_id", "register_id" },
                unique: true,
                filter: "status = 'Open'");

            migrationBuilder.CreateIndex(
                name: "ix_stock_discrepancy_tenant_detected",
                table: "stock_discrepancy",
                columns: new[] { "tenant_id", "detected_at" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_discrepancy_tenant_id_product_id",
                table: "stock_discrepancy",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_discrepancy_tenant_id_sale_id",
                table: "stock_discrepancy",
                columns: new[] { "tenant_id", "sale_id" });

            migrationBuilder.CreateIndex(
                name: "ix_stock_discrepancy_tenant_id_sale_line_id",
                table: "stock_discrepancy",
                columns: new[] { "tenant_id", "sale_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_tender_tenant_sale",
                table: "tender",
                columns: new[] { "tenant_id", "sale_id" });

            migrationBuilder.AddForeignKey(
                name: "fk_stock_movement_sale",
                table: "stock_movement",
                columns: new[] { "tenant_id", "sale_id" },
                principalTable: "sale",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Every table above is tenant-owned, so each needs RLS enabled, forced and
            // policied. An applied migration does not re-run, so this cannot be left to the
            // Phase 1 migration that introduced the mechanism -- the loop only sees tables
            // that exist when it runs. RowLevelSecurityTests.Every_tenant_owned_table_is_covered
            // fails the build if this call is missing.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropForeignKey(
                name: "fk_stock_movement_sale",
                table: "stock_movement");

            migrationBuilder.DropTable(
                name: "cash_movement");

            migrationBuilder.DropTable(
                name: "sale_sequence");

            migrationBuilder.DropTable(
                name: "stock_discrepancy");

            migrationBuilder.DropTable(
                name: "tender");

            migrationBuilder.DropTable(
                name: "sale_line");

            migrationBuilder.DropTable(
                name: "sale");

            migrationBuilder.DropTable(
                name: "shift");

            migrationBuilder.DropIndex(
                name: "ix_stock_movement_tenant_sale",
                table: "stock_movement");

            migrationBuilder.DropUniqueConstraint(
                name: "ak_register_tenant_id_id",
                table: "register");

            // Apply, NOT Remove. RemoveTenantRowLevelSecurity() loops over every table with a
            // tenant_id column, so calling it here would strip the policies from the catalog
            // and the identity tables this migration never touched.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
