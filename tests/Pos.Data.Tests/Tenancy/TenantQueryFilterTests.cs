using Microsoft.EntityFrameworkCore;
using Pos.Core.Exceptions;
using Pos.Core.Tenancy;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Tenancy;

/// <summary>
/// Layer 2, read side: every tenant-owned query is filtered whether or not anybody
/// remembered to filter it.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantQueryFilterTests(PostgresFixture postgres)
{
    [Fact]
    public async Task An_entity_nobody_registered_is_still_filtered_by_tenant()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var marker = Guid.NewGuid().ToString();

        await using (var contextA = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantA)))
        {
            contextA.ThrowawayEntities.Add(new ThrowawayEntity { Label = marker });
            await contextA.SaveChangesAsync();
        }

        await using (var contextB = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantB)))
        {
            // ThrowawayEntity has no configuration, no migration and no HasQueryFilter call
            // anywhere. If the reflection loop in AppDbContext were removed, this returns
            // tenant A's row and the assertion below is the only thing that notices.
            var visible = await contextB.ThrowawayEntities.Where(t => t.Label == marker).ToListAsync();
            Assert.Empty(visible);
        }

        await using (var contextA = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantA)))
        {
            var visible = await contextA.ThrowawayEntities.Where(t => t.Label == marker).ToListAsync();
            Assert.Single(visible);
        }
    }

    [Fact]
    public async Task Querying_with_no_tenant_resolved_throws_rather_than_returning_everything()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        await using var context = ThrowawayEntityDbContext.Create(
            postgres.ConnectionString,
            new AmbientTenantContext());

        // "No tenant" must never degrade into "all tenants". The filter reads
        // CurrentTenantId, which throws, so the query cannot be composed at all.
        await Assert.ThrowsAsync<TenantNotResolvedException>(() => context.ThrowawayEntities.ToListAsync());
    }

    [Fact]
    public async Task Ignoring_query_filters_does_reach_other_tenants_which_is_why_it_needs_review()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var marker = Guid.NewGuid().ToString();

        await using (var contextA = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantA)))
        {
            contextA.ThrowawayEntities.Add(new ThrowawayEntity { Label = marker });
            await contextA.SaveChangesAsync();
        }

        await using var contextB = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantB));

        // Pinned deliberately: IgnoreQueryFilters() is a real hole, which is why CLAUDE.md
        // requires an explicit justification for every use in review. Layer 3 (RLS) is what
        // stops this one in the deployed application; here the app connects as the owner.
        var leaked = await contextB.ThrowawayEntities.IgnoreQueryFilters().Where(t => t.Label == marker).ToListAsync();
        Assert.Single(leaked);
    }

    [Fact]
    public async Task The_tenant_table_itself_is_not_filtered()
    {
        // Tenant is not tenant-owned: login resolves it by slug before any tenant exists,
        // so a filter here would make logging in impossible.
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var count = await scoped.Db.Tenants.CountAsync();

        Assert.True(count >= 0);
    }

    private static AmbientTenantContext Resolved(Guid tenantId)
    {
        var context = new AmbientTenantContext();
        context.Resolve(tenantId);
        return context;
    }
}
