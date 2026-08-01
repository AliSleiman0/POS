using Pos.Core.Entities;

namespace Pos.Data.Tests.Catalog;

/// <summary>The ids of one written catalog, so a test can reach for the row it wants.</summary>
internal sealed record SeededCatalog(
    Guid TaxClassId,
    Guid CategoryId,
    Guid ProductId,
    Guid BarcodeId,
    Guid StockItemId,
    Guid StockMovementId);

/// <summary>
/// Writes a minimal but complete catalog — tax class, category, product, barcode, stock
/// item — into whatever tenant the context is scoped to.
/// </summary>
/// <remarks>
/// <b>Saved in three passes, and that is not a style choice.</b> There are no navigation
/// properties, so EF has no relationship to fix up: a product's <c>Id</c> stays
/// <c>Guid.Empty</c> until <c>TenantSaveChangesInterceptor</c> stamps it during
/// <c>SaveChanges</c>. Adding a product and its barcode in one call would write
/// <c>ProductId = Guid.Empty</c> and fail the foreign key. Principals first, then read the
/// ids back, then dependents.
/// </remarks>
internal static class CatalogGraph
{
    public static async Task<SeededCatalog> WriteAsync(
        AppDbContext db,
        string sku = "SKU-1001",
        string barcode = "5099999000011")
    {
        var taxClass = new TaxClass { Name = "Standard", Rate = 0.2300m };
        var category = new Category { Name = "Grocery", SortOrder = 10 };

        db.TaxClasses.Add(taxClass);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var product = new Product
        {
            Sku = sku,
            Name = "Still Water 500ml",
            CategoryId = category.Id,
            TaxClassId = taxClass.Id,
            UnitPrice = 1.2000m,
            CostPrice = 0.5500m,
        };

        db.Products.Add(product);
        await db.SaveChangesAsync();

        var code = new Barcode { ProductId = product.Id, Code = barcode, IsPrimary = true };
        var stock = new StockItem { ProductId = product.Id, OnHand = 12.0000m, ReorderPoint = 4m };

        // The receipt that put the twelve on the shelf, so on_hand agrees with its ledger
        // from the start. A fixture whose cache and ledger disagreed would make every rebuild
        // assertion pass for the wrong reason.
        var movement = new StockMovement
        {
            ProductId = product.Id,
            Type = StockMovementType.Receive,
            Quantity = 12.0000m,
            Reason = "Opening stock",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        db.Barcodes.Add(code);
        db.StockItems.Add(stock);
        db.StockMovements.Add(movement);
        await db.SaveChangesAsync();

        return new SeededCatalog(taxClass.Id, category.Id, product.Id, code.Id, stock.Id, movement.Id);
    }
}
