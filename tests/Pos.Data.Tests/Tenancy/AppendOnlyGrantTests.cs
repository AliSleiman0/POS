using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Core.Entities;
using Pos.Data.Tests.Infrastructure;
using Pos.TestSupport;

namespace Pos.Data.Tests.Tenancy;

/// <summary>
/// Append-only, enforced by the database rather than by convention.
/// </summary>
/// <remarks>
/// Every other append-only table in the schema — <c>stock_movement</c>, <c>sale</c> — relies
/// on no code path existing that would rewrite it. That is a real guarantee and it is not
/// this one: an audit log's whole value is that the people it records cannot edit it, so a
/// missing <c>UPDATE</c> path has to be a missing <i>privilege</i>, not a missing feature.
/// <para>
/// <b>This is not free, and that is why the test exists.</b> The RowLevelSecurity migration
/// runs <c>ALTER DEFAULT PRIVILEGES … GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO
/// pos_app</c> for the role that runs every later migration, so <c>audit_entry</c> was
/// created with update and delete and had to have them revoked by hand. Without that revoke
/// nothing anywhere would have failed.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AppendOnlyGrantTests(PostgresFixture postgres)
{
    [Theory]
    [InlineData("SELECT", true)]
    [InlineData("INSERT", true)]
    [InlineData("UPDATE", false)]
    [InlineData("DELETE", false)]
    public async Task The_application_role_may_only_read_and_append(string privilege, bool expected)
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT has_table_privilege(@role, 'audit_entry', @privilege)";
        command.Parameters.AddWithValue("role", AppRoleBootstrap.RoleName);
        command.Parameters.AddWithValue("privilege", privilege);

        var granted = (bool)(await command.ExecuteScalarAsync())!;

        // SELECT and INSERT are asserted alongside the two that matter, so a revoke that went
        // too far — or a table nobody can write at all — fails here rather than surfacing as
        // a broken audit trail the first time somebody voids a sale.
        Assert.Equal(expected, granted);
    }

    [Fact]
    public async Task The_application_role_cannot_rewrite_an_entry()
    {
        var tenantId = Guid.NewGuid();
        var entryId = await AppendAsync(tenantId);

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        // The counterpart to the privilege query above, and the half that proves the grant
        // actually bites: a catalog check passes just as happily against a privilege Postgres
        // is not enforcing.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            scoped.Db.Database.ExecuteSqlRawAsync(
                "UPDATE audit_entry SET action = 'SaleVoided' WHERE id = {0}",
                entryId));

        Assert.Equal("42501", exception.SqlState);
    }

    [Fact]
    public async Task The_application_role_cannot_delete_an_entry()
    {
        var tenantId = Guid.NewGuid();
        var entryId = await AppendAsync(tenantId);

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        // The one an insider would reach for. Rewriting an entry leaves a row that disagrees
        // with itself; deleting it leaves nothing to notice at all.
        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            scoped.Db.Database.ExecuteSqlRawAsync(
                "DELETE FROM audit_entry WHERE id = {0}",
                entryId));

        Assert.Equal("42501", exception.SqlState);
    }

    /// <summary>Writes one entry as the tenant that owns it, through the ordinary path.</summary>
    private async Task<Guid> AppendAsync(Guid tenantId)
    {
        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        var entry = new AuditEntry
        {
            Action = AuditAction.StockAdjusted,
            EntityType = nameof(Product),
            EntityId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
        };

        scoped.Db.AuditEntries.Add(entry);
        await scoped.Db.SaveChangesAsync();

        return entry.Id;
    }
}
