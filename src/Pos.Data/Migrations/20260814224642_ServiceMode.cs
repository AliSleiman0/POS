using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <inheritdoc />
    public partial class ServiceMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "service_mode",
                table: "tenant",
                type: "text",
                nullable: false,
                defaultValue: "Retail");

            migrationBuilder.AddCheckConstraint(
                name: "ck_tenant_service_mode_allowed",
                table: "tenant",
                sql: "\"service_mode\" IN ('Retail', 'Restaurant')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_tenant_service_mode_allowed",
                table: "tenant");

            migrationBuilder.DropColumn(
                name: "service_mode",
                table: "tenant");
        }
    }
}
