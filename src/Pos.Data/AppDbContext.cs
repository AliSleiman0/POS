using Microsoft.EntityFrameworkCore;

namespace Pos.Data;

/// <summary>
/// The application's EF Core context.
/// </summary>
/// <remarks>
/// Entities arrive in Phase 1 (tenancy) and Phase 2 (catalog). This milestone
/// establishes the model-wide conventions, because they must apply to every
/// entity that follows rather than being remembered per-property:
///
///   - money maps to numeric(19,4) — see docs/DATA-MODEL.md#money--rounding
///   - snake_case tables and columns
///   - UTC timestamptz for all instants
///
/// Phase 1.2 adds tenant query filters and the SaveChanges interceptor here.
/// </remarks>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    /// <summary>Money precision: numeric(19,4). Applied model-wide below.</summary>
    internal const int MoneyPrecision = 19;

    /// <summary>
    /// Four decimal places, not two. Gives room for unit prices like 0.1650 and
    /// for tax-inclusive back-calculation without accumulating rounding error.
    /// </summary>
    internal const int MoneyScale = 4;

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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // One IEntityTypeConfiguration<T> per entity, discovered by assembly scan,
        // so adding an entity never means editing this method.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
