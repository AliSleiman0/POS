using Pos.Core.Exceptions;
using Pos.Core.Tenancy;

namespace Pos.Core.Tests.Tenancy;

/// <summary>
/// The tenant context is the root of the isolation mechanism: if it can silently
/// produce "no tenant" or be re-pointed mid-scope, every layer above it inherits
/// the hole. These tests pin the failure modes rather than the happy path.
/// </summary>
public sealed class TenantContextTests
{
    [Fact]
    public void Unresolved_context_throws_rather_than_returning_an_empty_guid()
    {
        var context = new AmbientTenantContext();

        Assert.False(context.IsResolved);

        // The point of the whole type: Guid.Empty here would produce a query filter
        // that quietly matches nothing instead of an error anyone would notice.
        Assert.Throws<TenantNotResolvedException>(() => context.TenantId);
    }

    [Fact]
    public void Resolving_makes_the_tenant_readable()
    {
        var tenantId = Guid.NewGuid();
        var context = new AmbientTenantContext();

        context.Resolve(tenantId);

        Assert.True(context.IsResolved);
        Assert.Equal(tenantId, context.TenantId);
    }

    [Fact]
    public void Resolving_the_same_tenant_twice_is_allowed()
    {
        // Login resolves the tenant from the slug, then the token middleware resolves it
        // again from the freshly issued claim. Same tenant, so this must not throw.
        var tenantId = Guid.NewGuid();
        var context = new AmbientTenantContext();

        context.Resolve(tenantId);
        context.Resolve(tenantId);

        Assert.Equal(tenantId, context.TenantId);
    }

    [Fact]
    public void Resolving_a_second_different_tenant_throws()
    {
        var context = new AmbientTenantContext();
        context.Resolve(Guid.NewGuid());

        var ex = Assert.Throws<InvalidOperationException>(() => context.Resolve(Guid.NewGuid()));
        Assert.Contains("already resolved", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolving_an_empty_guid_throws()
    {
        var context = new AmbientTenantContext();

        Assert.Throws<ArgumentException>(() => context.Resolve(Guid.Empty));
    }

    [Fact]
    public void System_context_is_not_a_tenant_and_still_refuses_to_produce_one()
    {
        var context = new SystemTenantContext();

        Assert.True(context.IsSystemContext);
        Assert.False(context.IsResolved);

        // Opt-in "no tenant" is not god mode. Touching tenant-owned data under it fails.
        Assert.Throws<TenantNotResolvedException>(() => context.TenantId);
    }

    [Fact]
    public void Ambient_context_is_never_a_system_context()
    {
        // Guards against a future refactor collapsing the two: if "unresolved" ever
        // starts reading as "system", an unauthenticated request becomes a superuser.
        Assert.False(new AmbientTenantContext().IsSystemContext);
    }
}
