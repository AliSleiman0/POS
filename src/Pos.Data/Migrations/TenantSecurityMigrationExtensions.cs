using Microsoft.EntityFrameworkCore.Migrations;

namespace Pos.Data.Migrations;

/// <summary>
/// Applies row-level security to every table that carries a <c>tenant_id</c> column.
/// </summary>
/// <remarks>
/// <b>Call this from any migration that adds a tenant-owned table.</b> The loop is
/// idempotent and cheap, so calling it when nothing changed costs nothing — whereas
/// forgetting it leaves a table with no policy, which behaves exactly like working
/// software until someone reads across tenants.
/// <para>
/// It is a loop over the catalog rather than a list of table names because a list is
/// correct on the day it is written and silently incomplete afterwards. What it cannot do
/// is run itself: a migration that has already been applied does not re-run, so a table
/// introduced later needs this called again in <i>that</i> migration.
/// <c>RowLevelSecurityTests.Every_tenant_owned_table_is_covered</c> is what fails when
/// somebody forgets — deliberately loud, and deliberately not a code review's job.
/// </para>
/// </remarks>
public static class TenantSecurityMigrationExtensions
{
    /// <summary>The policy name carried by every tenant table.</summary>
    public const string PolicyName = "tenant_isolation";

    /// <summary>
    /// The catalog-driven DO block, exposed so tests can apply exactly the SQL the
    /// migrations apply rather than a second copy that drifts from it.
    /// </summary>
    /// <remarks>
    /// <c>FORCE</c> matters as much as <c>ENABLE</c>: without it the table owner silently
    /// bypasses its own policy, so if the application ever connects as the owner every
    /// policy is in place and none of them does anything.
    /// <para>
    /// <c>nullif(..., '')</c> is not decoration. <c>current_setting(..., true)</c> gives
    /// NULL when the setting was never assigned, but a <i>reset</i> session variable comes
    /// back as the empty string — and <c>''::uuid</c> raises rather than yielding NULL. Both
    /// have to collapse to NULL so that "no tenant" means "no rows", which is the safe
    /// direction to fail in.
    /// </para>
    /// <para>
    /// <c>WITH CHECK</c> as well as <c>USING</c>: without it a raw INSERT or UPDATE could
    /// write a row stamped with another tenant's id even though it could never read one
    /// back. Isolation covering only reads is half a mechanism.
    /// </para>
    /// </remarks>
    public const string ApplySql = """
        DO $$
        DECLARE target text;
        BEGIN
          FOR target IN
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND a.attname = 'tenant_id'
              AND NOT a.attisdropped
          LOOP
            EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', target);
            EXECUTE format('ALTER TABLE public.%I FORCE ROW LEVEL SECURITY', target);
            EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON public.%I', target);
            EXECUTE format(
              'CREATE POLICY tenant_isolation ON public.%I'
              ' USING (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)'
              ' WITH CHECK (tenant_id = nullif(current_setting(''app.tenant_id'', true), '''')::uuid)',
              target);
          END LOOP;
        END $$;
        """;

    /// <summary>Removes the policies again. The <c>Down</c> half of <see cref="ApplySql"/>.</summary>
    public const string RemoveSql = """
        DO $$
        DECLARE target text;
        BEGIN
          FOR target IN
            SELECT c.relname
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND a.attname = 'tenant_id'
              AND NOT a.attisdropped
          LOOP
            EXECUTE format('DROP POLICY IF EXISTS tenant_isolation ON public.%I', target);
            EXECUTE format('ALTER TABLE public.%I NO FORCE ROW LEVEL SECURITY', target);
            EXECUTE format('ALTER TABLE public.%I DISABLE ROW LEVEL SECURITY', target);
          END LOOP;
        END $$;
        """;

    /// <summary>Enables, forces and policies every tenant-owned table.</summary>
    public static MigrationBuilder ApplyTenantRowLevelSecurity(this MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql(ApplySql);

        return migrationBuilder;
    }

    /// <summary>Drops the policies from every tenant-owned table.</summary>
    public static MigrationBuilder RemoveTenantRowLevelSecurity(this MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);

        migrationBuilder.Sql(RemoveSql);

        return migrationBuilder;
    }
}
