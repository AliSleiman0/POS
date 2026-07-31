using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Core.Auditing;
using Pos.Core.Tenancy;
using Pos.Data.Interceptors;

namespace Pos.Data;

/// <summary>
/// Registers the data layer. Keeps Npgsql configuration in one place so the API's
/// composition root does not need to know the provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the DbContext, the tenancy services it depends on, and the interceptors
    /// that enforce tenant scoping on write.
    /// </summary>
    /// <remarks>
    /// <see cref="ITenantContext"/> and <see cref="ICurrentActor"/> are registered with
    /// <c>TryAdd</c>, so a host that has its own (the API registers an HTTP-backed actor)
    /// must register it <b>before</b> calling this.
    /// </remarks>
    public static IServiceCollection AddPosData(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        // Time enters through TimeProvider, never DateTimeOffset.UtcNow, so audit stamps
        // and business-day arithmetic can be tested at a fixed instant.
        services.TryAddSingleton(TimeProvider.System);

        // Scoped and resolvable as itself, because the pre-authentication paths (login by
        // slug, refresh, device token) need to call Resolve() on the concrete type.
        services.AddScoped<AmbientTenantContext>();
        services.TryAddScoped<ITenantContext>(sp => sp.GetRequiredService<AmbientTenantContext>());
        services.TryAddScoped<ICurrentActor, SystemActor>();

        services.AddScoped<TenantSaveChangesInterceptor>();
        services.AddScoped<TenantConnectionInterceptor>();

        services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            options
                .UseNpgsql(connectionString, npgsql =>
                {
                    // Transient network faults are normal against managed Postgres.
                    // NOTE: this retries idempotent reads safely, but a retry strategy
                    // cannot be combined with a user-initiated transaction without
                    // explicit handling — Phase 3.6 wraps its sale transaction in an
                    // execution strategy for exactly this reason.
                    npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);

                    // Migrations live in Pos.Data, not the startup project.
                    npgsql.MigrationsAssembly(typeof(AppDbContext).Assembly.FullName);
                })
                // Resolved per scope so the interceptors see this request's tenant.
                .AddInterceptors(
                    serviceProvider.GetRequiredService<TenantSaveChangesInterceptor>(),
                    // Publishes app.tenant_id to the session on every connection open, which
                    // is what the RLS policies read. Session-scoped and safe only because
                    // Npgsql resets pooled connections — see the interceptor's remarks
                    // before touching anything about pooling in the connection string.
                    serviceProvider.GetRequiredService<TenantConnectionInterceptor>())
                // snake_case tables and columns, applied model-wide rather than
                // per-property. See docs/DATA-MODEL.md#conventions.
                .UseSnakeCaseNamingConvention());

        return services;
    }
}
