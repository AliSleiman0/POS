using System.Linq.Expressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data.Identity;

namespace Pos.Data;

/// <summary>
/// The application's EF Core context, and layer 2 of the tenant isolation described in
/// docs/ARCHITECTURE.md#multi-tenancy.
/// </summary>
/// <remarks>
/// Model-wide conventions (money as numeric(19,4), snake_case, timestamptz) are set here
/// once rather than remembered per property. Tenant query filters are applied to every
/// <see cref="ITenantOwned"/> type by reflection.
/// </remarks>
public class AppDbContext : IdentityDbContext<
    ApplicationUser,
    ApplicationRole,
    Guid,
    ApplicationUserClaim,
    ApplicationUserRole,
    ApplicationUserLogin,
    ApplicationRoleClaim,
    ApplicationUserToken>
{
    private readonly ITenantContext _tenantContext;

    public AppDbContext(DbContextOptions<AppDbContext> options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>
    /// For a derived context. The test suite uses one to add a throwaway entity and prove
    /// that the query filter reaches an entity nobody registered anywhere — which is the
    /// only way to demonstrate that the reflection below actually works, rather than that
    /// the entities we remembered to write filters for are filtered.
    /// </summary>
    protected AppDbContext(DbContextOptions options, ITenantContext tenantContext)
        : base(options)
    {
        _tenantContext = tenantContext;
    }

    /// <summary>Money precision: numeric(19,4). Applied model-wide below.</summary>
    internal const int MoneyPrecision = 19;

    /// <summary>
    /// Four decimal places, not two. Gives room for unit prices like 0.1650 and
    /// for tax-inclusive back-calculation without accumulating rounding error.
    /// </summary>
    internal const int MoneyScale = 4;

    /// <summary>
    /// The tenant every query is filtered by.
    /// </summary>
    /// <remarks>
    /// Referenced from inside the generated query filters, so EF re-reads it per query
    /// against the executing context instance — a filter built once at model time still
    /// tracks the current request's tenant.
    /// <para>
    /// It deliberately <b>throws</b> when no tenant is resolved. A query issued outside a
    /// tenant is a bug, and the alternative (returning everything, or nothing) is a bug
    /// that ships.
    /// </para>
    /// </remarks>
    public Guid CurrentTenantId => _tenantContext.TenantId;

    /// <summary>The tenant list itself. Not tenant-owned: see <see cref="Tenant"/>.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every decimal in the model is money or a quantity, and both want
        // numeric(19,4). Setting it as a convention means a new property cannot
        // be mapped at the provider default (numeric(18,2) here) by omission —
        // which would silently truncate a unit price's 3rd and 4th decimals.
        configurationBuilder.Properties<decimal>().HavePrecision(MoneyPrecision, MoneyScale);
        configurationBuilder.Properties<decimal?>().HavePrecision(MoneyPrecision, MoneyScale);

        // UTC everywhere, per invariant 8. timestamptz is Npgsql's default for
        // DateTimeOffset; stating it here makes the intent explicit and covers
        // DateTime properties if any ever appear.
        configurationBuilder.Properties<DateTimeOffset>().HaveColumnType("timestamptz");
        configurationBuilder.Properties<DateTimeOffset?>().HaveColumnType("timestamptz");

        base.ConfigureConventions(configurationBuilder);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Identity first, so our configurations can replace the indexes it declares —
        // notably the platform-wide unique index on the user name, which would stop one
        // person holding an account at two tenants.
        base.OnModelCreating(builder);

        // .NET 10's Identity adds a WebAuthn passkey table. We do not do WebAuthn, and an
        // unused table is not free here: it would be the one table in the schema with no
        // tenant column, no query filter and no RLS policy — the exception that makes
        // "every table is scoped" stop being a checkable statement. Dropping it entirely
        // is cleaner than scoping something nothing writes to.
        builder.Ignore<IdentityUserPasskey<Guid>>();

        // One IEntityTypeConfiguration<T> per entity, discovered by assembly scan,
        // so adding an entity never means editing this method.
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Last, so it sees every entity type either of the above introduced.
        ApplyTenantQueryFilters(builder);
    }

    /// <summary>
    /// Applies <c>e =&gt; e.TenantId == CurrentTenantId</c> to every mapped
    /// <see cref="ITenantOwned"/> type.
    /// </summary>
    /// <remarks>
    /// By reflection, and not one hand-written <c>HasQueryFilter</c> per entity, because
    /// the hand-written version works right up until someone adds the eleventh entity and
    /// forgets — at which point nothing fails, no test goes red, and the data simply
    /// leaks. This loop cannot be forgotten by an entity that does not exist yet.
    /// </remarks>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType) || entityType.BaseType is not null)
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");

            // Expression.Constant(this) is the documented EF pattern for a context-instance
            // filter: EF swaps the captured context for the one actually executing the
            // query, so the value is read per query rather than frozen into the cached model.
            var body = Expression.Equal(
                Expression.Property(parameter, nameof(ITenantOwned.TenantId)),
                Expression.Property(Expression.Constant(this), nameof(CurrentTenantId)));

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }
}
