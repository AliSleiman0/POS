using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// <c>idempotency_record</c>: the stored response that makes a retry replay instead of
    /// repeat.
    /// </summary>
    /// <remarks>
    /// <c>ux_idempotency_record_tenant_key</c> <b>is</b> the mechanism, not a safety net behind
    /// a lookup. Two concurrent retries both miss the read and both insert; the loser blocks on
    /// this index until the winner commits, at which point the winner's row is visible to
    /// re-read and replay. A check-then-insert in application code cannot do that.
    /// <para>
    /// <c>response_body</c> is <c>text</c> rather than <c>jsonb</c> deliberately. Postgres
    /// reorders object keys and drops duplicates when parsing jsonb, so a replayed body would
    /// not be byte-identical to the one the client was first given — and "the same response" is
    /// the entire promise.
    /// </para>
    /// </remarks>
    public partial class Idempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.CreateTable(
                name: "idempotency_record",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<Guid>(type: "uuid", nullable: false),
                    endpoint = table.Column<string>(type: "text", nullable: false),
                    request_hash = table.Column<string>(type: "text", nullable: false),
                    response_status = table.Column<int>(type: "integer", nullable: false),
                    response_body = table.Column<string>(type: "text", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    tenant_id = table.Column<Guid>(type: "uuid", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    created_by = table.Column<Guid>(type: "uuid", nullable: true),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_idempotency_record", x => x.id);
                    table.CheckConstraint("ck_idempotency_record_endpoint_length", "length(\"endpoint\") <= 200");
                    table.CheckConstraint("ck_idempotency_record_request_hash_length", "length(\"request_hash\") <= 64");
                });

            migrationBuilder.CreateIndex(
                name: "ux_idempotency_record_tenant_key",
                table: "idempotency_record",
                columns: new[] { "tenant_id", "key" },
                unique: true);

            // idempotency_record is tenant-owned, so it needs RLS enabled, forced and
            // policied like every other table. An applied migration does not re-run, so the
            // Phase 1 loop cannot have covered a table that did not exist yet.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropTable(
                name: "idempotency_record");

            // Apply, NOT Remove — RemoveTenantRowLevelSecurity() loops over every table
            // carrying a tenant_id and would strip the policies this migration never touched.
            migrationBuilder.ApplyTenantRowLevelSecurity();
        }
    }
}
