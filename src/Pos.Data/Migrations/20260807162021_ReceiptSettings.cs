using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address_line",
                table: "tenant",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_footer",
                table: "tenant",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "receipt_header",
                table: "tenant",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tax_number",
                table: "tenant",
                type: "text",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_address_line_length",
                table: "tenant",
                sql: "length(\"address_line\") <= 500");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_receipt_footer_length",
                table: "tenant",
                sql: "length(\"receipt_footer\") <= 1000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_receipt_header_length",
                table: "tenant",
                sql: "length(\"receipt_header\") <= 1000");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_tax_number_length",
                table: "tenant",
                sql: "length(\"tax_number\") <= 64");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_address_line_length",
                table: "tenant");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_receipt_footer_length",
                table: "tenant");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_receipt_header_length",
                table: "tenant");

            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_tax_number_length",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "address_line",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "receipt_footer",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "receipt_header",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "tax_number",
                table: "tenant");
        }
    }
}
