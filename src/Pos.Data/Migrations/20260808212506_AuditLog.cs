using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class AuditLog : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "audit_entry",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    action = table.Column<string>(type: "text", nullable: false),
                    entity_type = table.Column<string>(type: "text", nullable: false),
                    entity_id = table.Column<Guid>(type: "uuid", nullable: false),
                    before = table.Column<string>(type: "jsonb", nullable: true),
                    after = table.Column<string>(type: "jsonb", nullable: true),
                    actor_id = table.Column<Guid>(type: "uuid", nullable: true),
                    register_id = table.Column<Guid>(type: "uuid", nullable: true),
                    occurred_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_entry", x => x.id);
                    table.CheckConstraint("ck_audit_entry_action_allowed", "\"action\" IN ('PriceOverridden', 'DiscountApplied', 'SaleVoided', 'RefundIssued', 'ReceiptIssued', 'StockAdjusted', 'EmployeeCreated', 'EmployeeDeactivated', 'RoleChanged', 'PinReset', 'DeviceEnrolled', 'DeviceRevoked', 'SettingsChanged', 'ShiftClosed', 'AuthorizationRefused')");
                    table.CheckConstraint("ck_audit_entry_entity_type_length", "length(\"entity_type\") <= 64");
                    table.ForeignKey(
                        name: "fk_audit_entry_register",
                        columns: x => new { x.tenant_id, x.register_id },
                        principalTable: "register",
                        principalColumns: new[] { "tenant_id", "id" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entry_tenant_id_register_id",
                table: "audit_entry",
                columns: new[] { "tenant_id", "register_id" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entry_tenant_occurred_at",
                table: "audit_entry",
                columns: new[] { "tenant_id", "occurred_at" });

            // Hand-written, and load-bearing. The RowLevelSecurity migration ran
            //   ALTER DEFAULT PRIVILEGES FOR ROLE <owner> IN SCHEMA public
            //     GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app
            // for the same role that runs this migration — so audit_entry has just been
            // created WITH update and delete, and "the app's DB role cannot rewrite history"
            // would be false while looking true. Nothing else in the schema notices, because
            // no other table needs the grant withheld. See AppendOnlyGrantTests.
            migrationBuilder.Sql("REVOKE UPDATE, DELETE ON audit_entry FROM pos_app;");

            // A new tenant-owned table gets no policy for free: an applied migration does not
            // re-run, so RLS is per-migration work. RowLevelSecurityTests fails the build
            // without this.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "audit_entry");

            // Apply, not Remove. Remove is catalog-driven and would strip the policy from
            // every remaining tenant table on the way back down.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
