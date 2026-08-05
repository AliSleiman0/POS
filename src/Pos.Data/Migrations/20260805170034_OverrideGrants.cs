using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// Adds <c>override_grant</c>: a manager's single-use authorisation for a privileged
    /// action taken on a cashier's session.
    /// </summary>
    /// <remarks>
    /// Purely additive — one new table, no column dropped or retyped, so nothing existing is at
    /// risk. The <see cref="TenantSecurityMigrationExtensions.ApplyTenantRowLevelSecurity"/>
    /// call is the part that is easy to forget and expensive to omit: a tenant-owned table with
    /// no policy behaves exactly like working software until somebody reads across tenants.
    /// <c>RowLevelSecurityTests.Every_tenant_owned_table_is_covered</c> is what catches it.
    /// </remarks>
    public partial class OverrideGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "override_grant",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    token_hash = table.Column<string>(type: "text", nullable: false),
                    policies = table.Column<string[]>(type: "text[]", nullable: false),
                    register_id = table.Column<Guid>(type: "uuid", nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    consumed_by_sale_id = table.Column<Guid>(type: "uuid", nullable: true),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_override_grant", x => x.id);
                    table.CheckConstraint("ck_override_grant_token_hash_length", "length(\"token_hash\") <= 44");
                });

            migrationBuilder.CreateIndex(
                name: "ix_override_grant_tenant_expires_at",
                table: "override_grant",
                columns: new[] { "tenant_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "ix_override_grant_tenant_user",
                table: "override_grant",
                columns: new[] { "tenant_id", "user_id" });

            migrationBuilder.CreateIndex(
                name: "ux_override_grant_tenant_hash",
                table: "override_grant",
                columns: new[] { "tenant_id", "token_hash" },
                unique: true);

            // The new table carries tenant_id, so it needs the policy. The block is
            // catalog-driven and idempotent, so re-applying it to the existing tables costs
            // nothing.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "override_grant");

            // Apply, not Remove — see the remarks on TenantSecurityMigrationExtensions. Remove
            // is catalog-driven too, so it would strip the policy from every remaining tenant
            // table and leave no migration behind to restore them.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
