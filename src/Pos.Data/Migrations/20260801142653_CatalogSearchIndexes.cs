using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// Gives <c>GET /products?q=</c> an index it can actually use, and the other two catalog
    /// lists the ordering index their cursor seeks into.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read the generated SQL, not this file.</b> <c>dotnet ef migrations script</c>
    /// shows the extensions being created before the index that needs them, and shows the
    /// index as <c>USING gin (tenant_id, name gin_trgm_ops)</c>. The C# above expresses that
    /// through two annotations and is much harder to be sure about.
    /// </para>
    /// <para>
    /// <b>No <c>ApplyTenantRowLevelSecurity()</c> here, deliberately.</b> That helper is a
    /// catalog-driven loop that enables, forces and re-policies every table carrying a
    /// <c>tenant_id</c>, and the reason <c>CatalogAndInventory</c> calls it in both
    /// directions is that it creates five such tables in <c>Up</c> and drops them in
    /// <c>Down</c>. This migration creates no table and drops none, so the loop would drop
    /// and recreate a policy on every tenant table to arrive back where it started — a lock
    /// on each one during deploy, in exchange for nothing. Coverage is not resting on
    /// anyone remembering the convention either way:
    /// <c>RowLevelSecurityTests.Every_tenant_owned_table_is_covered</c> enumerates the
    /// catalog and fails the build over an uncovered table. <b>A migration that does add a
    /// tenant-owned table must call it</b> — and in <c>Down</c> call <c>Apply</c>, never
    /// <c>Remove</c>, for the reason spelled out at the foot of <c>CatalogAndInventory</c>.
    /// </para>
    /// </remarks>
    public partial class CatalogSearchIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both are trusted extensions in PG 13+: CREATE on the database is enough and no
            // superuser is involved, which is what makes this deployable against managed
            // Postgres in Phase 8.2. Emitted ahead of the index below, which needs both.
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "ix_tax_class_tenant_name",
                table: "tax_class",
                columns: new[] { "tenant_id", "name" });

            // The one that matters. ILIKE '%q%' cannot use ix_product_tenant_name at any
            // width, so without this every ?q= sequential-scans the catalog. tenant_id leads
            // because the query filter puts `tenant_id = @p` on every query and
            // DatabaseSchemaTests fails an index on a tenant table that does not lead with
            // it; btree_gin is what supplies GIN's operator class for the uuid column so a
            // single index can carry both.
            migrationBuilder.CreateIndex(
                name: "ix_product_tenant_name_trgm",
                table: "product",
                columns: new[] { "tenant_id", "name" })
                .Annotation("Npgsql:IndexMethod", "gin")
                .Annotation("Npgsql:IndexOperators", new[] { "", "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "ix_category_tenant_name",
                table: "category",
                columns: new[] { "tenant_id", "name" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_tax_class_tenant_name",
                table: "tax_class");

            migrationBuilder.DropIndex(
                name: "ix_product_tenant_name_trgm",
                table: "product");

            migrationBuilder.DropIndex(
                name: "ix_category_tenant_name",
                table: "category");

            // Drops the extensions with them. Safe because nothing else in the schema uses
            // either one — if a later migration comes to depend on pg_trgm, it declares the
            // extension itself and this Down stops being the only thing holding it up.
            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:btree_gin", ",,")
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");
        }
    }
}
