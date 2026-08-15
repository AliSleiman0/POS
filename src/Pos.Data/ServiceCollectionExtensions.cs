using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pos.Core.Auditing;
using Pos.Core.Inventory;
using Pos.Core.Sales;
using Pos.Core.Tenancy;
using Pos.Data.Auditing;
using Pos.Data.Interceptors;
using Pos.Data.Inventory;
using Pos.Data.Orders;
using Pos.Data.Reporting;
using Pos.Data.Sales;
using Pos.Data.Shifts;

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

        // Managed hosts hand out `postgres://user:pass@host/db`; Npgsql speaks keyword form.
        // Accepting both here means every entry point — the API, the seeder, the onboarding
        // command, a maintenance job — takes whatever the platform gave, rather than each
        // one documenting a conversion an operator does by hand under time pressure.
        connectionString = PostgresConnectionString.Normalize(connectionString);

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

        // The port Core declares for the stock ledger. Registered here rather than in the API
        // so that a host with no HTTP in it — the seeder, a future maintenance job — gets a
        // working ledger from AddPosData alone.
        services.AddScoped<IStockLedger, StockLedger>();

        // The second port, on the same reasoning: committing a sale is a transaction with a
        // row lock, a counter and a concurrency token in it, not a save.
        services.AddScoped<ISaleWriter, SaleWriter>();

        // The third port, and the reason it is one: an audit entry has to land in the same
        // transaction as the thing it describes, so the writers above need to reach it — and
        // Pos.Data cannot see a type declared in Pos.Api, which is why the idempotency
        // context's shape (pass the DbContext in) does not work here.
        services.AddScoped<IAuditLog, AuditLog>();

        // Not behind a Core port, unlike the three above: there is no rule here Core needs to
        // own. The arithmetic is already pure in ShiftArithmetic, and what is left is three
        // queries and a lock.
        services.AddScoped<ShiftWriter>();

        // Registered on the same reasoning, and with one more of its own: an order is working
        // state rather than money, so there is not even a financial invariant here for Core to
        // own. What it does hold is a counter upsert and a row lock, which is why it is a writer
        // and not endpoint code.
        services.AddScoped<OrderWriter>();

        // Reads only, and behind no port either: aggregating money has to be raw SQL because
        // EF cannot sum a value-converted property, and a port would exist only to hide a type
        // from a project that already references this one.
        services.AddScoped<ReportQueries>();

        return services;
    }
}
