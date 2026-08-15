using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class OrderBills : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "order_bill",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_id = table.Column<Guid>(type: "uuid", nullable: false),
                    bill_number = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<string>(type: "text", nullable: false),
                    client_transaction_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tip_amount = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    paid_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    paid_by = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_bill", x => x.id);
                    table.UniqueConstraint("ak_order_bill_tenant_id_id", x => new { x.tenant_id, x.id });
                    table.CheckConstraint("ck_order_bill_status_allowed", "\"status\" IN ('Open', 'Paid', 'Voided')");
                    table.ForeignKey(
                        name: "fk_order_bill_order",
                        columns: x => new { x.tenant_id, x.order_id },
                        principalTable: "customer_order",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_bill_sale",
                        columns: x => new { x.tenant_id, x.sale_id },
                        principalTable: "sale",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_bill_line",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_bill_id = table.Column<Guid>(type: "uuid", nullable: false),
                    order_line_id = table.Column<Guid>(type: "uuid", nullable: false),
                    quantity = table.Column<decimal>(type: "numeric(19,4)", precision: 19, scale: 4, nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_order_bill_line", x => x.id);
                    table.CheckConstraint("ck_order_bill_line_quantity_positive", "quantity > 0");
                    table.ForeignKey(
                        name: "fk_order_bill_line_bill",
                        columns: x => new { x.tenant_id, x.order_bill_id },
                        principalTable: "order_bill",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_order_bill_line_order_line",
                        columns: x => new { x.tenant_id, x.order_line_id },
                        principalTable: "customer_order_line",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_order_bill_tenant_id_sale_id",
                table: "order_bill",
                columns: new[] { "tenant_id", "sale_id" });

            migrationBuilder.CreateIndex(
                name: "ux_order_bill_tenant_client_transaction_id",
                table: "order_bill",
                columns: new[] { "tenant_id", "client_transaction_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_order_bill_tenant_order_number",
                table: "order_bill",
                columns: new[] { "tenant_id", "order_id", "bill_number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_order_bill_line_tenant_order_line",
                table: "order_bill_line",
                columns: new[] { "tenant_id", "order_line_id" });

            migrationBuilder.CreateIndex(
                name: "ux_order_bill_line_tenant_bill_line",
                table: "order_bill_line",
                columns: new[] { "tenant_id", "order_bill_id", "order_line_id" },
                unique: true);

            // Two new tenant-owned tables, so the catalog-driven loop has to run again.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_bill_line");

            migrationBuilder.DropTable(
                name: "order_bill");

            // Apply, not Remove: Remove is catalog-driven and would strip every tenant table
            // rather than the two this migration created.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
