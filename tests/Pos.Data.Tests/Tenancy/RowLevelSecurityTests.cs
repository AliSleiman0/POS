using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data.Migrations;
using Pos.Data.Tests.Infrastructure;
using Pos.TestSupport;

namespace Pos.Data.Tests.Tenancy;

/// <summary>
/// Layer 3: the isolation that holds when application code is wrong.
/// </summary>
/// <remarks>
/// Query filters (layer 2) do not apply to <c>FromSqlRaw</c>, <c>ExecuteSqlRaw</c>, Dapper
/// or a hand-written report query — and a report is exactly where somebody reaches for raw
/// SQL. Every test here therefore connects as <b>pos_app</b>, which is <c>NOBYPASSRLS</c>.
/// Run as the container's owner account they would all pass without a single policy
/// existing, because a superuser bypasses row-level security unconditionally.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class RowLevelSecurityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Every_tenant_owned_table_is_covered()
    {
        var tables = await ReadSecurityCatalogAsync();

        // Not a fixed list: whatever tables exist with a tenant_id column, all of them must
        // be covered. This is the test that fails in Phase 2 when someone adds a table and
        // does not call ApplyTenantRowLevelSecurity() in its migration — loudly, at build
        // time, rather than as a leak nobody notices.
        Assert.NotEmpty(tables);

        // The three an attacker would most like to read across tenants. Named explicitly so
        // that a catalog query returning an empty set cannot make this test vacuous.
        Assert.Contains("application_user", tables.Select(t => t.Table), StringComparer.Ordinal);
        Assert.Contains("refresh_token", tables.Select(t => t.Table), StringComparer.Ordinal);
        Assert.Contains("register", tables.Select(t => t.Table), StringComparer.Ordinal);

        var uncovered = tables
            .Where(t => !t.Enabled || !t.Forced || !t.HasPolicy)
            .Select(t => $"{t.Table} (enabled={t.Enabled}, forced={t.Forced}, policy={t.HasPolicy})")
            .ToArray();

        // FORCE is checked alongside ENABLE because without it the table owner silently
        // bypasses its own policy — the policies would all be present and none would apply.
        Assert.True(
            uncovered.Length == 0,
            "Tables with a tenant_id column but no complete RLS coverage: " + string.Join(", ", uncovered));
    }

    [Fact]
    public async Task Raw_SQL_cannot_read_another_tenants_rows()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var registerId = await InsertRegisterAsync(tenantA, "Till A");

        await using (var asB = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantB))
        {
            // SqlQueryRaw, not a DbSet query: EF composes global query filters on top of
            // FromSqlRaw, so a DbSet-based "raw" query would prove layer 2 all over again.
            // This is the path a report writer actually takes, with layer 2 absent.
            var visible = await asB.Db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM register WHERE id = {0}", registerId)
                .SingleAsync();

            Assert.Equal(0, visible);
        }

        await using (var asA = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantA))
        {
            // The other half, and the one that stops this test passing for the wrong reason:
            // without it, an insert that silently failed would look exactly like isolation.
            var visible = await asA.Db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM register WHERE id = {0}", registerId)
                .SingleAsync();

            Assert.Equal(1, visible);
        }
    }

    [Fact]
    public async Task IgnoreQueryFilters_reaches_nothing_once_RLS_is_the_layer_underneath()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var marker = Guid.NewGuid().ToString();

        await using (var contextA = ThrowawayEntityDbContext.Create(postgres.AppConnectionString, Resolved(tenantA)))
        {
            contextA.ThrowawayEntities.Add(new ThrowawayEntity { Label = marker });
            await contextA.SaveChangesAsync();
        }

        await using var contextB = ThrowawayEntityDbContext.Create(postgres.AppConnectionString, Resolved(tenantB));

        // The companion to TenantQueryFilterTests.Ignoring_query_filters_does_reach_other_tenants,
        // which pins the same call leaking when connected as the owner. That is what makes
        // IgnoreQueryFilters() a review item rather than a breach: layer 2 is gone here, and
        // the row is still invisible.
        var leaked = await contextB.ThrowawayEntities
            .IgnoreQueryFilters()
            .Where(t => t.Label == marker)
            .ToListAsync();

        Assert.Empty(leaked);
    }

    [Fact]
    public async Task A_pooled_connection_does_not_inherit_the_previous_scopes_tenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        var registerId = await InsertRegisterAsync(tenantA, "Till A");

        // Max Pool Size=1 removes the luck from this. Without it the second scope probably
        // gets the same physical connection and the test probably means something; with it,
        // it is the same connection by construction. app.tenant_id is a *session* variable,
        // so this is the one test that fails if Npgsql ever stops resetting pooled
        // connections — which is what "No Reset On Close=true" or multiplexing would do.
        var singleConnection = new NpgsqlConnectionStringBuilder(postgres.AppConnectionString)
        {
            MaxPoolSize = 1,
            MinPoolSize = 1,
            ApplicationName = nameof(A_pooled_connection_does_not_inherit_the_previous_scopes_tenant),
        }.ConnectionString;

        await using (var asA = ScopedDbContext.ForTenant(singleConnection, tenantA))
        {
            var count = await asA.Db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM register WHERE id = {0}", registerId)
                .SingleAsync();

            Assert.Equal(1, count);
        }

        await using (var asB = ScopedDbContext.ForTenant(singleConnection, tenantB))
        {
            var count = await asB.Db.Database
                .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM register WHERE id = {0}", registerId)
                .SingleAsync();

            Assert.Equal(0, count);
        }
    }

    [Fact]
    public async Task A_scope_with_no_tenant_reads_nothing_rather_than_everything()
    {
        var tenantA = Guid.NewGuid();
        await InsertRegisterAsync(tenantA, "Till A");

        await using var scoped = ScopedDbContext.WithoutTenant(postgres.AppConnectionString);

        // The failure direction matters. current_setting returns NULL when unset and the
        // empty string when reset, and '' cast to uuid raises — so the policy collapses both
        // to NULL, which matches no rows. "No tenant" must never mean "every tenant".
        var visible = await scoped.Db.Database
            .SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM register")
            .SingleAsync();

        Assert.Equal(0, visible);
    }

    [Fact]
    public async Task A_raw_insert_stamped_with_another_tenant_is_rejected()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        await using var asA = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantA);

        // WITH CHECK, not just USING. Isolation that only covers reads lets a raw INSERT
        // write a row into a tenant it can never read back — which is worse than a leak,
        // because the victim's data is now wrong and nothing points at where it came from.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            asA.Db.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO register (id, tenant_id, name, is_active, created_at)
                VALUES ({0}, {1}, 'Smuggled', true, now())
                """,
                Guid.NewGuid(),
                tenantB));

        // 42501 — insufficient_privilege, which is how Postgres reports a row-security
        // violation on write.
        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_application_role_cannot_create_tables()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.AppConnectionString);

        // pos_app runs the application; it does not run migrations. A role that can create
        // a table can create one with no policy on it, which walks straight around
        // everything above.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            scoped.Db.Database.ExecuteSqlRawAsync("CREATE TABLE rls_probe (id uuid PRIMARY KEY)"));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_application_role_does_not_bypass_row_level_security()
    {
        await using var connection = new NpgsqlConnection(postgres.AppConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user";

        // Every assertion in this class is worthless if this is ever true — a BYPASSRLS role
        // reads every policy as though it were not there, and the whole suite goes green.
        Assert.False((bool)(await command.ExecuteScalarAsync())!);
        Assert.Equal(AppRoleBootstrap.RoleName, connection.UserName);
    }

    private static AmbientTenantContext Resolved(Guid tenantId)
    {
        var context = new AmbientTenantContext();
        context.Resolve(tenantId);
        return context;
    }

    /// <summary>Writes a register through the ordinary path, as the tenant that owns it.</summary>
    private async Task<Guid> InsertRegisterAsync(Guid tenantId, string name)
    {
        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        var register = new Register { Name = name };

        scoped.Db.Registers.Add(register);
        await scoped.Db.SaveChangesAsync();

        return register.Id;
    }

    private sealed record TableSecurity(string Table, bool Enabled, bool Forced, bool HasPolicy);

    /// <summary>Reads RLS state straight from the catalog, as the owner so nothing is hidden.</summary>
    private async Task<List<TableSecurity>> ReadSecurityCatalogAsync()
    {
        var results = new List<TableSecurity>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT DISTINCT
                   c.relname,
                   c.relrowsecurity,
                   c.relforcerowsecurity,
                   EXISTS (
                     SELECT 1 FROM pg_policy p
                     WHERE p.polrelid = c.oid AND p.polname = '{TenantSecurityMigrationExtensions.PolicyName}'
                   )
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN pg_attribute a ON a.attrelid = c.oid
            WHERE n.nspname = 'public'
              AND c.relkind = 'r'
              AND a.attname = 'tenant_id'
              AND NOT a.attisdropped
            """;

        await using var reader = await command.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            results.Add(new TableSecurity(
                reader.GetString(0),
                reader.GetBoolean(1),
                reader.GetBoolean(2),
                reader.GetBoolean(3)));
        }

        return results;
    }
}
