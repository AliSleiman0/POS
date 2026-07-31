using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Pos.Data;

/// <summary>
/// Registers the data layer. Keeps Npgsql configuration in one place so the API's
/// composition root does not need to know the provider.
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPosData(this IServiceCollection services, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        services.AddDbContext<AppDbContext>(options =>
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
                // snake_case tables and columns, applied model-wide rather than
                // per-property. See docs/DATA-MODEL.md#conventions.
                .UseSnakeCaseNamingConvention());

        return services;
    }
}
