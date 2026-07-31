using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Pos.Data.Migrations
{
    /// <summary>
    /// Row-level security: layer 3 of the tenant isolation, and the only one that survives
    /// application code being wrong.
    /// </summary>
    /// <remarks>
    /// EF's global query filters do not apply to <c>FromSqlRaw</c>, <c>ExecuteSqlRaw</c>,
    /// Dapper or a hand-written report query — and reporting is exactly where someone
    /// reaches for raw SQL. This is what holds when that happens.
    /// <para>
    /// Everything here is driven by a loop over "tables with a <c>tenant_id</c> column"
    /// rather than a list written out by hand. A list is correct the day it is written and
    /// silently incomplete the day someone adds the eleventh table — and the failure mode is
    /// a table with no policy, which reads as working software.
    /// </para>
    /// </remarks>
    public partial class RowLevelSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            // Fail here, clearly, rather than three statements later with "role pos_app does
            // not exist". Creating the role needs a password, so it is a bootstrap step and
            // not part of this migration — see docker/postgres-init/01-app-role.sh.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'pos_app') THEN
                    RAISE EXCEPTION 'Role "pos_app" does not exist. It is created by a bootstrap step rather than a migration, because creating a login role requires a password. Locally: docker compose down -v && docker compose up -d.';
                  END IF;
                END $$;
                """);

            // Grants on everything, not only the tenant tables: pos_app also reads `tenant`
            // (the tenant list, which by definition carries no tenant_id) and the role table.
            migrationBuilder.Sql("""
                GRANT USAGE ON SCHEMA public TO pos_app;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO pos_app;
                GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO pos_app;
                """);

            // Tables created by *later* migrations are covered without anyone remembering to
            // come back here. Default privileges are recorded per granting role, so this has
            // to name the role actually running the migration rather than a literal.
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                  EXECUTE format(
                    'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app',
                    current_user);
                  EXECUTE format(
                    'ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO pos_app',
                    current_user);
                END $$;
                """);

            migrationBuilder.ApplyTenantRowLevelSecurity();
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            System.ArgumentNullException.ThrowIfNull(migrationBuilder);

            migrationBuilder.RemoveTenantRowLevelSecurity();
        }
    }
}
