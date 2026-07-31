using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Data;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// Checklist items 3 and 4, taken inside the running application rather than over HTTP.
/// </summary>
/// <remarks>
/// <c>Pos.Data.Tests.RowLevelSecurityTests</c> proves the same two layers against a fixture
/// it builds itself. What it cannot prove is that the <i>hosted application</i> is wired the
/// same way — a suite can have perfect data-layer coverage while the API connects as the
/// owner and bypasses all of it. Everything here runs in a scope taken from the API's own
/// service provider, on the API's own connection string.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class HostScopeIsolationTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_DbContext_from_the_hosts_own_scope_sees_one_tenant()
    {
        var world = await factory.IsolationWorldAsync();

        await factory.AsTenantAsync(world.B.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var ids = await db.Registers.Select(r => r.Id).ToListAsync();

            // Both tenants hold a "Front Counter" and a "Back Counter". Four rows here would
            // mean the query filter is not being applied in the host's scope.
            Assert.Equal(world.B.RegisterIds.Order(), ids.Order());
        });
    }

    [Fact]
    public async Task Raw_SQL_from_the_hosts_own_scope_cannot_reach_another_tenant()
    {
        var world = await factory.IsolationWorldAsync();

        await factory.AsTenantAsync(world.B.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // SqlQueryRaw composes no query filter, which is the point: this is the path a
            // report writer takes, with layer 2 absent and only row-level security left.
            var otherTenant = await db.Database
                .SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM register WHERE id = {0}",
                    world.A.FrontCounter.Id)
                .SingleAsync();

            var ownTenant = await db.Database
                .SqlQueryRaw<int>(
                    "SELECT count(*)::int AS \"Value\" FROM register WHERE id = {0}",
                    world.B.FrontCounter.Id)
                .SingleAsync();

            Assert.Equal(0, otherTenant);

            // Without this half, a world that failed to seed would look exactly like
            // isolation working.
            Assert.Equal(1, ownTenant);
        });
    }

    [Fact]
    public async Task The_hosted_application_connects_as_a_role_that_cannot_bypass_row_level_security()
    {
        var world = await factory.IsolationWorldAsync();

        await factory.AsTenantAsync(world.B.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var bypasses = await db.Database
                .SqlQueryRaw<bool>(
                    "SELECT rolbypassrls AS \"Value\" FROM pg_roles WHERE rolname = current_user")
                .SingleAsync();

            // The assertion the rest of this file depends on. A superuser reads every policy
            // as though it were not there, so if the API ever connects as the migration owner
            // the raw-SQL test above passes while proving nothing at all.
            Assert.False(bypasses);
        });
    }
}
