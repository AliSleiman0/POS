using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// Adds <c>sale.recorded_at</c> — when the server wrote the row, as against
    /// <c>completed_at</c>'s when the trade happened. The two diverge only for a sale queued
    /// offline and replayed later, which Phase 9 introduces.
    /// </summary>
    /// <remarks>
    /// <b>Hand-edited, and the generated version was wrong in a way that would not have
    /// shown.</b> EF produced a single <c>AddColumn</c> with a non-null default of
    /// <c>0001-01-01</c>, so every sale ever taken would have claimed it was recorded at the
    /// dawn of the calendar — a value nothing would ever have complained about, since nothing
    /// reads the column for old rows. The three steps below replace it: add nullable, backfill
    /// from <c>completed_at</c>, then make it non-null.
    /// <para>
    /// <b>The backfill has to step around row-level security, and this is the trap.</b> Every
    /// tenant table is <c>FORCE ROW LEVEL SECURITY</c>, which applies to the table owner —
    /// which is who migrations connect as. With no <c>app.tenant_id</c> set, the policy's
    /// <c>USING</c> clause matches nothing, so a plain <c>UPDATE sale SET ...</c> reports
    /// success having touched <b>zero rows</b>. It is the same shape as the Phase 8.5 finding
    /// where <c>pg_dump</c> left a plausible backup with a table's data missing: the failure is
    /// silent, and the artefact it leaves looks fine. So <c>FORCE</c> is lifted for the length
    /// of the statement and restored immediately.
    /// </para>
    /// <para>
    /// <b>This is invisible on a development machine, which is why it is spelled out.</b> The
    /// local <c>pos</c> role is a superuser with <c>BYPASSRLS</c>, so the naive statement
    /// updates every row here and the migration looks correct. Managed Postgres gives no
    /// superuser — the Phase 8.5 lesson — so the deployed owner is subject to <c>FORCE</c> and
    /// the same statement matches nothing. Verified rather than assumed: against a table owned
    /// by a <c>NOSUPERUSER NOBYPASSRLS</c> role with this exact policy, the bare
    /// <c>UPDATE</c> reported <c>UPDATE 0</c> and the same statement with <c>FORCE</c> lifted
    /// reported <c>UPDATE 2</c>.
    /// </para>
    /// <para>
    /// The <c>SET NOT NULL</c> at the end is also the assertion. If the backfill had matched
    /// nothing, every row would still be null and this migration would fail loudly here rather
    /// than leaving a column full of wrong timestamps.
    /// </para>
    /// </remarks>
    public partial class OfflineSaleTimestamps : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "recorded_at",
                table: "sale",
                type: "timestamptz",
                nullable: true);

            // Every sale that already exists was rung online — there was no offline path before
            // this migration — so the moment it was recorded is exactly the moment it
            // completed. Not a guess standing in for missing data: for these rows it is the
            // true value.
            migrationBuilder.Sql(
                """
                ALTER TABLE public.sale NO FORCE ROW LEVEL SECURITY;
                UPDATE public.sale SET recorded_at = completed_at WHERE recorded_at IS NULL;
                ALTER TABLE public.sale FORCE ROW LEVEL SECURITY;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "recorded_at",
                table: "sale",
                type: "timestamptz",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamptz",
                oldNullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.DropColumn(
                name: "recorded_at",
                table: "sale");
        }
    }
}
