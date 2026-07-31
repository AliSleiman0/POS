using Microsoft.EntityFrameworkCore;
using Pos.Core.Auditing;
using Pos.Core.Tenancy;
using Pos.Data.Interceptors;
using Pos.Data.Migrations;

namespace Pos.Data.Tests.Infrastructure;

/// <summary>
/// An entity that exists only inside this test assembly. It has no
/// <c>IEntityTypeConfiguration</c>, no mention in <c>AppDbContext</c>, and no migration —
/// nothing anywhere registers it for tenant scoping.
/// </summary>
/// <remarks>
/// That is the entire point. Testing the filter against entities we wrote filters for
/// proves only that we wrote them. This proves the mechanism catches an entity whose
/// author did nothing except derive from <see cref="TenantEntity"/> — which is what
/// Phase 2 onward will actually do.
/// </remarks>
public sealed class ThrowawayEntity : TenantEntity
{
    public required string Label { get; set; }

    /// <summary>
    /// Present so the money convention can be asserted on an entity that configures
    /// nothing — which is the only way to show that a Phase 2 property will get
    /// numeric(19,4) without its author doing anything.
    /// </summary>
    public decimal Amount { get; set; }
}

/// <summary>
/// A context carrying <see cref="ThrowawayEntity"/> and nothing else new.
/// </summary>
public sealed class ThrowawayEntityDbContext(DbContextOptions options, ITenantContext tenantContext)
    : AppDbContext(options, tenantContext)
{
    public DbSet<ThrowawayEntity> ThrowawayEntities => Set<ThrowawayEntity>();

    /// <summary>
    /// Builds the context by hand — deliberately, so nothing in the application's DI
    /// wiring can be credited for the filter that is about to be asserted.
    /// </summary>
    public static ThrowawayEntityDbContext Create(string connectionString, ITenantContext tenantContext)
    {
        var options = new DbContextOptionsBuilder<ThrowawayEntityDbContext>()
            .UseNpgsql(connectionString)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(
                new TenantSaveChangesInterceptor(tenantContext, new SystemActor(), TimeProvider.System),
                // Publishes app.tenant_id, which the RLS policies read. Present here so a
                // hand-built context is subject to layer 3 exactly as the application is —
                // without it, an RLS assertion on this context would pass because no tenant
                // was ever set, which is the wrong reason to be green.
                new TenantConnectionInterceptor(tenantContext))
            .Options;

        return new ThrowawayEntityDbContext(options, tenantContext);
    }

    /// <summary>
    /// Creates the backing table. Not a migration: this entity must stay out of the
    /// application's schema, which is exactly why it is a fair test of the convention.
    /// </summary>
    public static async Task EnsureTableAsync(string connectionString)
    {
        await using var context = Create(connectionString, new SystemTenantContext());

        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS throwaway_entities (
                id          uuid PRIMARY KEY,
                tenant_id   uuid NOT NULL,
                label       text NOT NULL,
                amount      numeric(19,4) NOT NULL,
                created_at  timestamptz NOT NULL,
                created_by  uuid NULL,
                updated_at  timestamptz NULL,
                updated_by  uuid NULL
            );
            """);

        // The same SQL the migrations run, not a second copy of it. A table added by a
        // later phase gets its policy this way too — which is precisely what this entity
        // is standing in for. Grants come free: the RLS migration set ALTER DEFAULT
        // PRIVILEGES for the owner, so a table it creates afterwards is already reachable
        // by pos_app, and this table quietly proves that as well.
        await context.Database.ExecuteSqlRawAsync(TenantSecurityMigrationExtensions.ApplySql);
    }
}
