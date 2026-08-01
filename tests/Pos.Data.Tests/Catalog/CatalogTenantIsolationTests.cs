using Microsoft.EntityFrameworkCore;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Catalog;

/// <summary>
/// The catalog and inventory entities are invisible across tenants — demonstrated per
/// entity, on the application's own connection.
/// </summary>
/// <remarks>
/// The query filter is applied by reflection to everything implementing <c>ITenantOwned</c>,
/// so in principle this cannot fail for a new entity. In principle is the problem: the
/// filter is skipped for an entity type EF maps as owned or derived, and a configuration
/// that hand-wrote <c>HasQueryFilter</c> would silently replace it. This is the behavioural
/// check that the reflection in <c>TenantModelTests</c> reached every one of these types.
/// <para>
/// Run as <c>pos_app</c>, so a failure here means the query filter <i>and</i> row-level
/// security both let the row through.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class CatalogTenantIsolationTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_catalog_written_by_one_tenant_is_invisible_to_another()
    {
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();

        await using var ownerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, owner);
        var catalog = await CatalogGraph.WriteAsync(ownerScope.Db, "SKU-ISO", "5099999800011");

        await using var strangerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, stranger);
        var strangerDb = strangerScope.Db;

        // Reached for by id, not counted: a count of zero is also what an empty table
        // gives, whereas asking for the specific row that definitely exists elsewhere can
        // only come back null if something scoped the query.
        Assert.Null(await strangerDb.TaxClasses.FirstOrDefaultAsync(t => t.Id == catalog.TaxClassId));
        Assert.Null(await strangerDb.Categories.FirstOrDefaultAsync(c => c.Id == catalog.CategoryId));
        Assert.Null(await strangerDb.Products.FirstOrDefaultAsync(p => p.Id == catalog.ProductId));
        Assert.Null(await strangerDb.Barcodes.FirstOrDefaultAsync(b => b.Id == catalog.BarcodeId));
        Assert.Null(await strangerDb.StockItems.FirstOrDefaultAsync(s => s.Id == catalog.StockItemId));
        Assert.Null(await strangerDb.StockMovements.FirstOrDefaultAsync(m => m.Id == catalog.StockMovementId));

        // And the same five reads succeed for the tenant that wrote them, which is what
        // rules out "the rows were never written" as the reason the reads above came back
        // empty.
        Assert.NotNull(await ownerScope.Db.TaxClasses.FirstOrDefaultAsync(t => t.Id == catalog.TaxClassId));
        Assert.NotNull(await ownerScope.Db.Categories.FirstOrDefaultAsync(c => c.Id == catalog.CategoryId));
        Assert.NotNull(await ownerScope.Db.Products.FirstOrDefaultAsync(p => p.Id == catalog.ProductId));
        Assert.NotNull(await ownerScope.Db.Barcodes.FirstOrDefaultAsync(b => b.Id == catalog.BarcodeId));
        Assert.NotNull(await ownerScope.Db.StockItems.FirstOrDefaultAsync(s => s.Id == catalog.StockItemId));
        Assert.NotNull(await ownerScope.Db.StockMovements.FirstOrDefaultAsync(m => m.Id == catalog.StockMovementId));
    }

    [Fact]
    public async Task A_barcode_lookup_does_not_find_another_tenants_code()
    {
        var owner = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        const string Code = "5099999900011";

        await using var ownerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, owner);
        await CatalogGraph.WriteAsync(ownerScope.Db, "SKU-SCAN", Code);

        await using var strangerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, stranger);

        // The lookup 2.3 builds its endpoint on, and the one a leak would be most visible
        // in: scanning an item at one shop must not price it from another's catalog.
        Assert.Null(await strangerScope.Db.Barcodes.FirstOrDefaultAsync(b => b.Code == Code));
        Assert.NotNull(await ownerScope.Db.Barcodes.FirstOrDefaultAsync(b => b.Code == Code));
    }
}
