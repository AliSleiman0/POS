using Microsoft.EntityFrameworkCore;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Tenancy;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Schema;

/// <summary>
/// What putting <see cref="Money"/> on an entity actually bought, and what it cost.
/// </summary>
/// <remarks>
/// The cost is written down here rather than discovered in Phase 3.8 under time pressure:
/// EF cannot translate an aggregate over a value-converted property, so shift arithmetic has
/// to read its sums through raw SQL. <see cref="Summing_money_in_the_database_is_not_translatable"/>
/// pins that, so if a future EF version gains the ability, the test fails and somebody gets
/// to delete a workaround rather than carrying it forever.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class MoneyMappingTests(PostgresFixture postgres)
{
    [Fact]
    public void Money_properties_map_to_numeric_19_4_without_being_configured()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        // Properties<decimal>() matches on the CLR property type and does NOT cover Money.
        // Without its own convention entry this reads numeric(18,2) and a unit price of
        // 0.1650 stores as 0.17 — the same silent truncation the decimal sweep exists for.
        Assert.Equal("numeric(19,4)", ColumnTypeOf(context, nameof(ThrowawayEntity.Price)));
    }

    [Fact]
    public void A_nullable_money_property_maps_to_numeric_19_4_too()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        // Its own registration: Properties<Money>() does not cover Money?. Shift.CountedCash
        // and Product.CostPrice are both nullable, so a missing line here is a real column.
        Assert.Equal("numeric(19,4)", ColumnTypeOf(context, nameof(ThrowawayEntity.OptionalPrice)));
    }

    [Fact]
    public void The_converter_stores_the_amount_and_reads_it_back_unchanged()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        var property = context.Model
            .FindEntityType(typeof(ThrowawayEntity))!
            .FindProperty(nameof(ThrowawayEntity.Price))!;

        var converter = property.GetValueConverter();

        Assert.NotNull(converter);
        Assert.Equal(typeof(decimal), converter.ProviderClrType);
        Assert.Equal(0.1650m, converter.ConvertToProvider((Money)0.1650m));
        Assert.Equal((Money)0.1650m, converter.ConvertFromProvider(0.1650m));
    }

    [Fact]
    public void The_converter_does_not_round_on_the_way_to_the_database()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        var converter = context.Model
            .FindEntityType(typeof(ThrowawayEntity))!
            .FindProperty(nameof(ThrowawayEntity.Price))!
            .GetValueConverter()!;

        // Deliberately: rounding here would put a rounding rule in a layer nobody reads, and
        // the writer's explicit RoundToStorage() is where that decision is meant to be
        // visible. Postgres will round this to 1.0001 and that is the writer's problem to
        // have prevented, not the converter's to hide.
        Assert.Equal(1.00005m, converter.ConvertToProvider((Money)1.00005m));
    }

    [Fact]
    public async Task A_money_amount_survives_a_round_trip_through_postgres()
    {
        var tenant = await CreateTenantAsync();

        await using (var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            scoped.Db.Products.Add(new Product
            {
                Sku = "SKU-MONEY-ROUNDTRIP",
                Name = "Paper bag",
                TaxClassId = await SeedTaxClassAsync(scoped, tenant),

                // The fourth decimal is the point: it is a real seeded price, and it is what
                // numeric(18,2) would have quietly destroyed.
                UnitPrice = (Money)0.1650m,
                CostPrice = null,
            });

            await scoped.Db.SaveChangesAsync();
        }

        // A second scope, so the answer comes from the database rather than from the change
        // tracker still holding the instance that was written.
        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var stored = await reader.Db.Products
            .AsNoTracking()
            .FirstAsync(p => p.Sku == "SKU-MONEY-ROUNDTRIP");

        Assert.Equal((Money)0.1650m, stored.UnitPrice);
        Assert.Null(stored.CostPrice);
    }

    [Fact]
    public async Task Summing_money_in_the_database_is_not_translatable()
    {
        // The recorded cost of typing entity amounts as Money. EF has no SUM over a
        // value-converted property, so ShiftArithmetic reads its aggregates through
        // db.Database.SqlQuery<decimal> with an explicit tenant predicate (the query filter
        // does not compose over raw SQL) and RLS underneath as the second layer.
        //
        // If a future EF version translates this, the test fails and the workaround can go.
        var tenant = await CreateTenantAsync();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => scoped.Db.Products.SumAsync(p => p.UnitPrice.Amount));
    }

    private static string ColumnTypeOf(ThrowawayEntityDbContext context, string propertyName) =>
        context.Model
            .FindEntityType(typeof(ThrowawayEntity))!
            .FindProperty(propertyName)!
            .GetColumnType();

    /// <summary>A tenant of its own, so the round-trip assertions do not depend on run order.</summary>
    private async Task<Guid> CreateTenantAsync()
    {
        var tenantId = Guid.CreateVersion7();

        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        scoped.Db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Money Test Tenant",
            Slug = $"t-{tenantId:N}"[..20],
            CurrencyCode = "EUR",
            TimeZoneId = "Europe/Dublin",
        });

        await scoped.Db.SaveChangesAsync();

        return tenantId;
    }

    private static async Task<Guid> SeedTaxClassAsync(ScopedDbContext scoped, Guid tenantId)
    {
        var taxClass = new TaxClass { Name = "Standard", Rate = 0.2300m };

        scoped.Db.TaxClasses.Add(taxClass);
        await scoped.Db.SaveChangesAsync();

        Assert.Equal(tenantId, taxClass.TenantId);

        return taxClass.Id;
    }
}
