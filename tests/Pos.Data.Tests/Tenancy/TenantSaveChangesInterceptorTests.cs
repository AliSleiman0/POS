using Microsoft.EntityFrameworkCore;
using Pos.Core.Exceptions;
using Pos.Core.Tenancy;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Tenancy;

/// <summary>
/// Layer 2, write side. The query filter means these paths are unreachable through
/// ordinary code — they are reachable through attach-by-id, deserialised request bodies
/// and <c>IgnoreQueryFilters()</c>, which is precisely why the interceptor exists.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class TenantSaveChangesInterceptorTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Insert_is_stamped_with_the_ambient_tenant_and_audit_columns()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var tenantId = Guid.NewGuid();
        var label = Guid.NewGuid().ToString();

        await using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantId));

        var entity = new ThrowawayEntity { Label = label };
        context.ThrowawayEntities.Add(entity);
        await context.SaveChangesAsync();

        Assert.Equal(tenantId, entity.TenantId);
        Assert.NotEqual(default, entity.CreatedAt);
        Assert.NotEqual(Guid.Empty, entity.Id);

        // UUIDv7, so inserts land at the end of the index rather than scattering across it.
        Assert.Equal(7, entity.Id.Version);
    }

    [Fact]
    public async Task Insert_carrying_another_tenants_id_is_rejected_not_silently_corrected()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var ambient = Guid.NewGuid();
        var someoneElse = Guid.NewGuid();

        await using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(ambient));

        // The shape of a request body that arrived with a TenantId in it. Overwriting it
        // would turn an attempted cross-tenant write into a successful in-tenant one, with
        // nothing anywhere recording that it was tried.
        context.ThrowawayEntities.Add(new ThrowawayEntity { Label = "planted", TenantId = someoneElse });

        var ex = await Assert.ThrowsAsync<CrossTenantWriteException>(() => context.SaveChangesAsync());
        Assert.Equal(someoneElse, ex.AttemptedTenantId);
        Assert.Equal(ambient, ex.AmbientTenantId);
    }

    [Fact]
    public async Task Modifying_another_tenants_row_throws()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        var label = Guid.NewGuid().ToString();
        Guid rowId;

        await using (var ownerContext = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(owner)))
        {
            var entity = new ThrowawayEntity { Label = label };
            ownerContext.ThrowawayEntities.Add(entity);
            await ownerContext.SaveChangesAsync();
            rowId = entity.Id;
        }

        await using var attackerContext = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(attacker));

        // Loaded past the filter — the one way to get another tenant's row in hand.
        var stolen = await attackerContext.ThrowawayEntities.IgnoreQueryFilters().SingleAsync(t => t.Id == rowId);
        stolen.Label = "rewritten";

        await Assert.ThrowsAsync<CrossTenantWriteException>(() => attackerContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Deleting_another_tenants_row_throws()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var owner = Guid.NewGuid();
        var attacker = Guid.NewGuid();
        Guid rowId;

        await using (var ownerContext = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(owner)))
        {
            var entity = new ThrowawayEntity { Label = Guid.NewGuid().ToString() };
            ownerContext.ThrowawayEntities.Add(entity);
            await ownerContext.SaveChangesAsync();
            rowId = entity.Id;
        }

        await using var attackerContext = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(attacker));

        var stolen = await attackerContext.ThrowawayEntities.IgnoreQueryFilters().SingleAsync(t => t.Id == rowId);
        attackerContext.ThrowawayEntities.Remove(stolen);

        await Assert.ThrowsAsync<CrossTenantWriteException>(() => attackerContext.SaveChangesAsync());
    }

    [Fact]
    public async Task A_row_cannot_be_moved_to_another_tenant()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var owner = Guid.NewGuid();
        Guid rowId;

        await using (var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(owner)))
        {
            var entity = new ThrowawayEntity { Label = Guid.NewGuid().ToString() };
            context.ThrowawayEntities.Add(entity);
            await context.SaveChangesAsync();
            rowId = entity.Id;
        }

        await using var ownerContext = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(owner));

        var row = await ownerContext.ThrowawayEntities.SingleAsync(t => t.Id == rowId);

        // Checking only the row's *current* tenant would wave this through: it starts in
        // tenant and ends somewhere else, so the guard has to read the original value.
        row.TenantId = Guid.NewGuid();

        await Assert.ThrowsAsync<CrossTenantWriteException>(() => ownerContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Update_stamps_updated_columns_and_leaves_created_alone()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var tenantId = Guid.NewGuid();
        Guid rowId;
        DateTimeOffset createdAt;

        await using (var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantId)))
        {
            var entity = new ThrowawayEntity { Label = "before" };
            context.ThrowawayEntities.Add(entity);
            await context.SaveChangesAsync();
            rowId = entity.Id;
            createdAt = entity.CreatedAt;
        }

        await using (var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantId)))
        {
            var row = await context.ThrowawayEntities.SingleAsync(t => t.Id == rowId);
            row.Label = "after";

            // A caller trying to rewrite history. The audit columns are the first thing
            // anyone reads when a row looks wrong, so they are not the caller's to set.
            row.CreatedAt = DateTimeOffset.UnixEpoch;

            await context.SaveChangesAsync();
        }

        await using var verify = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantId));
        var saved = await verify.ThrowawayEntities.SingleAsync(t => t.Id == rowId);

        Assert.Equal("after", saved.Label);
        Assert.NotNull(saved.UpdatedAt);
        Assert.Equal(createdAt.ToUnixTimeMilliseconds(), saved.CreatedAt.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task Insert_with_no_tenant_resolved_and_no_explicit_tenant_throws()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        await using var context = ThrowawayEntityDbContext.Create(
            postgres.ConnectionString,
            new SystemTenantContext());

        context.ThrowawayEntities.Add(new ThrowawayEntity { Label = "orphan" });

        // The system context is allowed to insert tenant-owned rows, but only by naming
        // the tenant. It never gets a default, because a default is how an orphan row with
        // an empty tenant gets created and then matches an empty filter later.
        await Assert.ThrowsAsync<TenantNotResolvedException>(() => context.SaveChangesAsync());
    }

    [Fact]
    public async Task System_context_may_insert_when_it_names_the_tenant_explicitly()
    {
        await ThrowawayEntityDbContext.EnsureTableAsync(postgres.ConnectionString);

        var tenantId = Guid.NewGuid();
        var label = Guid.NewGuid().ToString();

        await using (var system = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext()))
        {
            // How provisioning creates a tenant's first rows before anyone can log in.
            system.ThrowawayEntities.Add(new ThrowawayEntity { Label = label, TenantId = tenantId });
            await system.SaveChangesAsync();
        }

        await using var scoped = ThrowawayEntityDbContext.Create(postgres.ConnectionString, Resolved(tenantId));
        Assert.Single(await scoped.ThrowawayEntities.Where(t => t.Label == label).ToListAsync());
    }

    private static AmbientTenantContext Resolved(Guid tenantId)
    {
        var context = new AmbientTenantContext();
        context.Resolve(tenantId);
        return context;
    }
}
