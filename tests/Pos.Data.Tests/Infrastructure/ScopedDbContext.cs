using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Auditing;
using Pos.Core.Tenancy;
using Pos.Data.Identity;

namespace Pos.Data.Tests.Infrastructure;

/// <summary>
/// A DbContext resolved through the real DI registration, scoped to one tenant.
/// </summary>
/// <remarks>
/// Built through <see cref="ServiceCollectionExtensions.AddPosData"/> rather than by
/// hand-constructing <c>DbContextOptions</c>, so a test exercises the same wiring the API
/// gets. A hand-built context that forgets the interceptor would pass tests the
/// application would fail.
/// </remarks>
public sealed class ScopedDbContext : IAsyncDisposable
{
    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private ScopedDbContext(ServiceProvider provider, AsyncServiceScope scope, AppDbContext db)
    {
        _provider = provider;
        _scope = scope;
        Db = db;
    }

    public AppDbContext Db { get; }

    /// <summary>The scope's services, for resolving <c>UserManager</c> and friends.</summary>
    public IServiceProvider Services => _scope.ServiceProvider;

    /// <summary>Opens a scope with <paramref name="tenantId"/> resolved as the ambient tenant.</summary>
    public static ScopedDbContext ForTenant(string connectionString, Guid tenantId, Guid? actorId = null)
    {
        var scoped = Create(connectionString, actorId);
        scoped._scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);
        return scoped;
    }

    /// <summary>Opens a scope with no tenant resolved — every tenant-owned read must fail.</summary>
    public static ScopedDbContext WithoutTenant(string connectionString)
        => Create(connectionString, actorId: null);

    private static ScopedDbContext Create(string connectionString, Guid? actorId)
    {
        var services = new ServiceCollection();

        if (actorId is not null)
        {
            services.AddScoped<ICurrentActor>(_ => new FixedActor(actorId.Value));
        }

        services.AddLogging();
        services.AddPosData(connectionString);
        services.AddPosIdentity();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateAsyncScope();

        return new ScopedDbContext(provider, scope, scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private sealed class FixedActor(Guid userId) : ICurrentActor
    {
        public Guid? UserId => userId;
    }
}
