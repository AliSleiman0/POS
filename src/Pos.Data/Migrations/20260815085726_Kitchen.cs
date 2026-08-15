using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class Kitchen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "station_id",
                table: "product",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "station_id",
                table: "category",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "station",
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
                    table.PrimaryKey("pk_station", x => x.id);
                    table.UniqueConstraint("ak_station_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_station_name_length", "length(\"name\") <= 60");
                });

            migrationBuilder.CreateTable(
                name: "kitchen_ticket",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    station_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course = table.Column<int>(type: "integer", nullable: false),
                    order_number = table.Column<long>(type: "bigint", nullable: false),
                    order_label = table.Column<string>(type: "text", nullable: false),
                    fired_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    fired_by = table.Column<Guid>(type: "uuid", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    bumped_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    bumped_by = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_ticket", x => x.id);
                    table.UniqueConstraint("ak_kitchen_ticket_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_kitchen_ticket_bumped_consistent", "(\"status\" = 'Bumped' AND bumped_at IS NOT NULL AND bumped_by IS NOT NULL)\nOR (\"status\" <> 'Bumped' AND bumped_at IS NULL AND bumped_by IS NULL)");
                    table.CheckConstraint("ck_kitchen_ticket_course_positive", "course >= 1");
                    table.CheckConstraint("ck_kitchen_ticket_order_label_length", "length(\"order_label\") <= 60");
                    table.CheckConstraint("ck_kitchen_ticket_status_allowed", "\"status\" IN ('Active', 'Bumped')");
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_order",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalTable: "customer_order",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_station",
                        columns: x => new { x.tenant_id, x.station_id },
                        principalTable: "station",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "kitchen_ticket_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    kitchen_ticket_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    line_number = table.Column<int>(type: "integer", nullable: false),
                    description = table.Column<string>(type: "text", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    seat_number = table.Column<int>(type: "integer", nullable: true),
                    modifier_text = table.Column<string>(type: "text", nullable: true),
                    note = table.Column<string>(type: "text", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_kitchen_ticket_line", x => x.id);
                    table.UniqueConstraint("ak_kitchen_ticket_line_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_kitchen_ticket_line_description_length", "length(\"description\") <= 200");
                    table.CheckConstraint("ck_kitchen_ticket_line_modifier_text_length", "length(\"modifier_text\") <= 500");
                    table.CheckConstraint("ck_kitchen_ticket_line_note_length", "length(\"note\") <= 200");
                    table.CheckConstraint("ck_kitchen_ticket_line_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_line_order_line",
                        columns: x => new { x.tenant_id, x.order_line_id },
                        principalTable: "customer_order_line",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_kitchen_ticket_line_ticket",
                        columns: x => new { x.tenant_id, x.kitchen_ticket_id },
                        principalTable: "kitchen_ticket",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_product_tenant_station",
                table: "product",
                columns: new[] { "tenant_id", "station_id" });

            migrationBuilder.CreateIndex(
                name: "ix_category_tenant_station",
                table: "category",
                columns: new[] { "tenant_id", "station_id" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_tenant_order_course",
                table: "kitchen_ticket",
                columns: new[] { "tenant_id", "order_id", "course" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_tenant_station_status_fired",
                table: "kitchen_ticket",
                columns: new[] { "tenant_id", "station_id", "status", "fired_at" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_line_tenant_order_line",
                table: "kitchen_ticket_line",
                columns: new[] { "tenant_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ix_kitchen_ticket_line_tenant_ticket_line_number",
                table: "kitchen_ticket_line",
                columns: new[] { "tenant_id", "kitchen_ticket_id", "line_number" });

            migrationBuilder.CreateIndex(
                name: "ix_station_tenant_sort",
                table: "station",
                columns: new[] { "tenant_id", "sort_order", "name" });

            migrationBuilder.CreateIndex(
                name: "ux_station_tenant_name",
                table: "station",
                columns: new[] { "tenant_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_category_station",
                table: "category",
                columns: new[] { "tenant_id", "station_id" },
                principalTable: "station",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "fk_product_station",
                table: "product",
                columns: new[] { "tenant_id", "station_id" },
                principalTable: "station",
                principalColumns: new[] { "tenant_id", "id" },
                onDelete: ReferentialAction.Restrict);

            // Three new tenant-owned tables — station, kitchen_ticket, kitchen_ticket_line — so
            // the catalog-driven loop has to run again here. An applied migration does not
            // re-run, so tables introduced in this one are covered only if this is called in it.
            // RowLevelSecurityTests.Every_tenant_owned_table_is_covered is what fails when
            // somebody forgets.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_category_station",
                table: "category");

            migrationBuilder.DropForeignKey(
                name: "fk_product_station",
                table: "product");

            migrationBuilder.DropTable(
                name: "kitchen_ticket_line");

            migrationBuilder.DropTable(
                name: "kitchen_ticket");

            migrationBuilder.DropTable(
                name: "station");

            migrationBuilder.DropIndex(
                name: "ix_product_tenant_station",
                table: "product");

            migrationBuilder.DropIndex(
                name: "ix_category_tenant_station",
                table: "category");

            migrationBuilder.DropColumn(
                name: "station_id",
                table: "product");

            migrationBuilder.DropColumn(
                name: "station_id",
                table: "category");

            // Apply, not Remove, for the reason the OrderModel migration states: Remove is
            // catalog-driven in the same way Apply is, so it would strip every tenant table
            // rather than the three this migration created. Re-applying after the drops
            // produces the right end state.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
