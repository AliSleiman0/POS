using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_order_sequence",
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
                    table.PrimaryKey("pk_customer_order_sequence", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "service_area",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
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
                    table.PrimaryKey("pk_service_area", x => x.id);
                    table.UniqueConstraint("ak_service_area_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_service_area_name_length", "length(\"name\") <= 60");
                });

            migrationBuilder.CreateTable(
                name: "dining_table",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    service_area_id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "text", nullable: false),
                    seats = table.Column<int>(type: "integer", nullable: false),
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
                    table.PrimaryKey("pk_dining_table", x => x.id);
                    table.UniqueConstraint("ak_dining_table_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_dining_table_name_length", "length(\"name\") <= 40");
                    table.CheckConstraint("ck_dining_table_seats_range", "seats >= 0");
                    table.ForeignKey(
                        name: "fk_dining_table_service_area",
                        columns: x => new { x.tenant_id, x.service_area_id },
                        principalTable: "service_area",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_order",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_number = table.Column<long>(type: "bigint", nullable: false),
                    type = table.Column<string>(type: "text", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    dining_table_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tab_name = table.Column<string>(type: "text", nullable: true),
                    register_id = table.Column<Guid>(type: "uuid", nullable: true),
                    opened_by = table.Column<Guid>(type: "uuid", nullable: false),
                    opened_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    cover_count = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    closed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    closed_by = table.Column<Guid>(type: "uuid", nullable: true),
                    abandon_reason = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_order", x => x.id);
                    table.UniqueConstraint("ak_customer_order_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_customer_order_abandon_reason_length", "length(\"abandon_reason\") <= 200");
                    table.CheckConstraint("ck_customer_order_note_length", "length(\"note\") <= 500");
                    table.CheckConstraint("ck_customer_order_status_allowed", "\"status\" IN ('Open', 'Closed', 'Abandoned')");
                    table.CheckConstraint("ck_customer_order_tab_name_length", "length(\"tab_name\") <= 60");
                    table.CheckConstraint("ck_customer_order_type_allowed", "\"type\" IN ('Table', 'Tab', 'Takeaway')");
                    table.ForeignKey(
                        name: "fk_customer_order_dining_table",
                        columns: x => new { x.tenant_id, x.dining_table_id },
                        principalTable: "dining_table",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_order_register",
                        columns: x => new { x.tenant_id, x.register_id },
                        principalTable: "register",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "customer_order_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    product_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    parent_order_line_id = table.Column<Guid>(type: "uuid", nullable: true),
                    description = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    unit_price = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tax_rate = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    discount_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    is_price_overridden = table.Column<bool>(type: "boolean", nullable: false),
                    overridden_by = table.Column<Guid>(type: "uuid", nullable: true),
                    course = table.Column<int>(type: "integer", nullable: false),
                    seat_number = table.Column<int>(type: "integer", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    status = table.Column<string>(type: "text", nullable: false),
                    fired_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    voided_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    voided_by = table.Column<Guid>(type: "uuid", nullable: true),
                    void_reason = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_customer_order_line", x => x.id);
                    table.UniqueConstraint("ak_customer_order_line_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_customer_order_line_course_positive", "course >= 1");
                    table.CheckConstraint("ck_customer_order_line_description_length", "length(\"description\") <= 200");
                    table.CheckConstraint("ck_customer_order_line_note_length", "length(\"note\") <= 200");
                    table.CheckConstraint("ck_customer_order_line_quantity_positive", "quantity > 0");
                    table.CheckConstraint("ck_customer_order_line_seat_positive", "seat_number IS NULL OR seat_number >= 1");
                    table.CheckConstraint("ck_customer_order_line_status_allowed", "\"status\" IN ('Pending', 'Fired', 'Voided')");
                    table.CheckConstraint("ck_customer_order_line_void_reason_length", "length(\"void_reason\") <= 200");
                    table.ForeignKey(
                        name: "fk_customer_order_line_order",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalTable: "customer_order",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_order_line_parent",
                        columns: x => new { x.tenant_id, x.parent_order_line_id },
                        principalTable: "customer_order_line",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_customer_order_line_product",
                        columns: x => new { x.tenant_id, x.product_id },
                        principalTable: "product",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_customer_order_tenant_id_register_id",
                table: "customer_order",
                columns: new[] { "tenant_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_order_tenant_status_opened_at",
                table: "customer_order",
                columns: new[] { "tenant_id", "status", "opened_at" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_order_tenant_order_number",
                table: "customer_order",
                columns: new[] { "tenant_id", "order_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_customer_order_tenant_table_open",
                table: "customer_order",
                columns: new[] { "tenant_id", "dining_table_id" },
                unique: true,
                filter: "status = 'Open' AND dining_table_id IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "ix_customer_order_line_tenant_id_product_id",
                table: "customer_order_line",
                columns: new[] { "tenant_id", "product_id" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_order_line_tenant_order_line_number",
                table: "customer_order_line",
                columns: new[] { "tenant_id", "order_id", "line_number" });

            migrationBuilder.CreateIndex(
                name: "ix_customer_order_line_tenant_parent",
                table: "customer_order_line",
                columns: new[] { "tenant_id", "parent_order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ux_customer_order_sequence_tenant",
                table: "customer_order_sequence",
                column: "tenant_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_dining_table_tenant_area_sort",
                table: "dining_table",
                columns: new[] { "tenant_id", "service_area_id", "sort_order" });

            migrationBuilder.CreateIndex(
                name: "ux_dining_table_tenant_name",
                table: "dining_table",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_service_area_tenant_sort",
                table: "service_area",
                columns: new[] { "tenant_id", "sort_order", "name" });

            // Five new tenant-owned tables, so the catalog-driven loop has to run again. A
            // migration that has already been applied does not re-run, so tables introduced
            // here are covered only if this is called here.
            // RowLevelSecurityTests.Every_tenant_owned_table_is_covered is what fails when
            // somebody forgets — deliberately loud, and deliberately not a code review's job.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_order_line");

            migrationBuilder.DropTable(
                name: "customer_order_sequence");

            migrationBuilder.DropTable(
                name: "customer_order");

            migrationBuilder.DropTable(
                name: "dining_table");

            migrationBuilder.DropTable(
                name: "service_area");

            // Apply, not Remove. Remove is catalog-driven in the same way Apply is, so it would
            // strip every tenant table rather than the five this migration created — leaving
            // sale, product and the Identity tables unprotected with no migration left to
            // restore them. Re-applying after the drops produces the right end state.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
