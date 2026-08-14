using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// What the offline catalog mirror needs from the database: a tombstone for a withdrawn
    /// barcode, and an index that makes "everything changed since" a range scan.
    /// </summary>
    /// <remarks>
    /// <b>The soft delete is for the mirror, not for an audit trail.</b> A till syncs
    /// incrementally, asking what changed since it last looked. A row deleted outright answers
    /// nothing at all — so a withdrawn code would never reach the till, which would go on
    /// scanning it at a price nobody authorised until somebody rebuilt the mirror from scratch.
    /// <para>
    /// The unique index has to become <b>filtered</b> in the same migration, not later: without
    /// it a withdrawn code can never be re-added, and re-adding is the ordinary case — a label
    /// was mis-typed and is being corrected. EF generated that half correctly.
    /// </para>
    /// <para>
    /// <b>The expression indexes are hand-written</b>, because EF has no API for them. The feed
    /// orders on <c>COALESCE(updated_at, created_at)</c>: <c>UpdatedAt</c> is null until a row
    /// is first edited, so ordering on it alone would sort every never-edited row into one
    /// undifferentiated null bucket that a keyset cannot page through. A composite index on the
    /// two columns does not serve an <c>ORDER BY</c> over their <c>COALESCE</c> — only an index
    /// on the expression itself does, and without one every sync is a sort of the whole table.
    /// </para>
    /// </remarks>
    public partial class CatalogSync : Migration
    {
        /// <summary>The tables <c>GET /catalog/sync</c> reads incrementally.</summary>
        private static readonly string[] SyncedTables = ["product", "barcode", "tax_class", "category"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropIndex(
                name: "ux_barcode_tenant_code",
                table: "barcode");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "deleted_at",
                table: "barcode",
                type: "timestamptz",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_barcode_tenant_code",
                table: "barcode",
                columns: new[] { "tenant_id", "code" },
                unique: true,
                filter: "deleted_at IS NULL");

            // "Which of this tenant's barcodes were withdrawn since <watermark>" — a small
            // list on every sync, and without the index a scan of every barcode the shop has.
            migrationBuilder.Sql(
                """
                CREATE INDEX ix_barcode_tenant_deleted_at ON public.barcode (tenant_id, deleted_at)
                WHERE deleted_at IS NOT NULL;
                """);

            foreach (var table in SyncedTables)
            {
                // Leading with tenant_id like every other index here: the global query filter
                // means every query is already tenant-scoped, so a leading tenant_id is what
                // makes an index usable rather than scanned. The id tiebreaker is what makes
                // the keyset's order total.
                migrationBuilder.Sql(
                    $"""
                    CREATE INDEX ix_{table}_tenant_changed ON public.{table}
                    (tenant_id, COALESCE(updated_at, created_at), id);
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            foreach (var table in SyncedTables)
            {
                migrationBuilder.Sql($"DROP INDEX IF EXISTS ix_{table}_tenant_changed;");
            }

            migrationBuilder.Sql("DROP INDEX IF EXISTS ix_barcode_tenant_deleted_at;");

            migrationBuilder.DropIndex(
                name: "ux_barcode_tenant_code",
                table: "barcode");

            /*
             * Withdrawn codes have to go before the unique index loses its filter.
             *
             * Rolling back re-imposes uniqueness across *every* row, and a product whose
             * mis-typed label was removed and re-added now holds two rows with the same code —
             * one of them a tombstone. The index would fail to build and the rollback would
             * stop halfway.
             *
             * Deleting them is right rather than merely expedient: without the column they are
             * rows that scan, and a schema with no soft delete has no way to express what they
             * are. FORCE row-level security is lifted for the statement for the same reason as
             * in OfflineSaleTimestamps — migrations connect as the owner, FORCE applies to the
             * owner, and with no app.tenant_id set the DELETE would match zero rows and report
             * success. Verified: the bare statement reports 0 against a NOSUPERUSER owner.
             */
            migrationBuilder.Sql(
                """
                ALTER TABLE public.barcode NO FORCE ROW LEVEL SECURITY;
                DELETE FROM public.barcode WHERE deleted_at IS NOT NULL;
                ALTER TABLE public.barcode FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.DropColumn(
                name: "deleted_at",
                table: "barcode");

            migrationBuilder.CreateIndex(
                name: "ux_barcode_tenant_code",
                table: "barcode",
                columns: new[] { "tenant_id", "code" },
                unique: true);
        }
    }
}
