using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class RestaurantAuditActions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_entry_action_allowed",
                table: "audit_entry");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_entry_action_allowed",
                table: "audit_entry",
                sql: "\"action\" IN ('PriceOverridden', 'DiscountApplied', 'SaleVoided', 'RefundIssued', 'ReceiptIssued', 'StockAdjusted', 'EmployeeCreated', 'EmployeeDeactivated', 'RoleChanged', 'PinReset', 'DeviceEnrolled', 'DeviceRevoked', 'SettingsChanged', 'ShiftClosed', 'AuthorizationRefused', 'OrderOpened', 'OrderLineVoided', 'OrderTransferred', 'OrderMerged', 'OrderAbandoned')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_audit_entry_action_allowed",
                table: "audit_entry");

            migrationBuilder.AddCheckConstraint(
                name: "ck_audit_entry_action_allowed",
                table: "audit_entry",
                sql: "\"action\" IN ('PriceOverridden', 'DiscountApplied', 'SaleVoided', 'RefundIssued', 'ReceiptIssued', 'StockAdjusted', 'EmployeeCreated', 'EmployeeDeactivated', 'RoleChanged', 'PinReset', 'DeviceEnrolled', 'DeviceRevoked', 'SettingsChanged', 'ShiftClosed', 'AuthorizationRefused')");
        }
    }
}
