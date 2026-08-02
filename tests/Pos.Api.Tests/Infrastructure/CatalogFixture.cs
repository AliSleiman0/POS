using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Core.Monetary;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>The ids of one tenant's seeded catalog.</summary>
/// <remarks>
/// Named after what each row <i>is for</i> rather than by index, because the manifest rows
/// and the margin tests reach for specific ones and <c>ProductIds[0]</c> tells a reader
/// nothing about why that product was the right victim.
/// </remarks>
public sealed record SeededCatalog(
    Guid StandardTaxClassId,
    Guid ZeroTaxClassId,
    Guid GroceryCategoryId,
    Guid CheeseCategoryId,
    Guid WaterProductId,
    Guid CoffeeProductId,
    Guid BagProductId,
    Guid WaterBarcodeId,
    Guid WaterMultipackBarcodeId,
    Guid CoffeeBarcodeId)
{
    public IReadOnlyList<Guid> ProductIds => [WaterProductId, CoffeeProductId, BagProductId];

    public IReadOnlyList<Guid> CategoryIds => [GroceryCategoryId, CheeseCategoryId];

    public IReadOnlyList<Guid> TaxClassIds => [StandardTaxClassId, ZeroTaxClassId];

    /// <summary>Everything <c>GET /products/{waterId}/barcodes</c> must return.</summary>
    public IReadOnlyList<Guid> WaterBarcodeIds => [WaterBarcodeId, WaterMultipackBarcodeId];

    /// <summary>
    /// Everything <c>GET /stock</c> must return: the products that track stock.
    /// </summary>
    /// <remarks>
    /// Not the same list as <see cref="ProductIds"/> — the carrier bag has
    /// <c>TrackStock = false</c> and must not appear, which is the assertion that would fail
    /// if the endpoint ever stopped filtering.
    /// </remarks>
    public IReadOnlyList<Guid> StockedProductIds => [WaterProductId, CoffeeProductId];
}

/// <summary>
/// Writes an identical catalog into a tenant.
/// </summary>
/// <remarks>
/// The same shape as <c>Pos.Data.Tests/Catalog/CatalogGraph.cs</c>, duplicated rather than
/// shared because the two test projects do not reference each other — and should not, since
/// one asserts about the database and the other about the API.
/// <para>
/// <b>Four saves, principals before dependents.</b> None of the catalog entities has a
/// navigation property, so EF does no foreign-key fixup: a category's <c>Id</c> stays
/// <see cref="Guid.Empty"/> until <c>TenantSaveChangesInterceptor</c> stamps it during
/// <c>SaveChanges</c>. Adding a child in the same call writes <c>Guid.Empty</c> into the
/// foreign key and fails.
/// </para>
/// </remarks>
public static class CatalogFixture
{
    public const string StandardTaxClassName = "Standard";
    public const string ZeroTaxClassName = "Zero";

    public const string GroceryCategoryName = "Grocery";
    public const string CheeseCategoryName = "Cheese";

    public const string WaterSku = "WATER-500";
    public const string CoffeeSku = "COFFEE-250";
    public const string BagSku = "BAG-1";

    public const string WaterName = "Still Water 500ml";
    public const string CoffeeName = "Coffee 250g";
    public const string BagName = "Carrier Bag";

    /// <summary>
    /// The water's cost price. A real value, and the margin tests depend on that: an
    /// "absent for a Cashier" assertion against a product with no cost passes whether the
    /// omission works or not.
    /// </summary>
    public const decimal WaterCostPrice = 0.5500m;

    public const decimal CoffeeCostPrice = 2.4000m;

    /// <summary>
    /// The water's own code and the code on a shrink-wrapped six-pack of it.
    /// </summary>
    /// <remarks>
    /// Two codes on one product because that is the case one-barcode-per-product modelling
    /// gets wrong, and a fixture with only one of them cannot notice.
    /// </remarks>
    public const string WaterBarcode = "5010000000011";

    public const string WaterMultipackBarcode = "5010000000028";

    public const string CoffeeBarcode = "5010000000103";

    /// <summary>The bag has no barcode, so the empty collection is a real case too.</summary>
    public const decimal WaterOnHand = 12.0000m;

    public const decimal WaterReorderPoint = 4.0000m;

    /// <summary>
    /// Below its reorder point on purpose, so <c>?belowReorderPoint=true</c> has one row to
    /// return and one to leave out rather than answering the same either way.
    /// </summary>
    public const decimal CoffeeOnHand = 3.0000m;

    public const decimal CoffeeReorderPoint = 5.0000m;

    public static async Task<SeededCatalog> WriteAsync(PosApiFactory factory, Guid tenantId)
    {
        ArgumentNullException.ThrowIfNull(factory);

        SeededCatalog? catalog = null;

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var standard = new TaxClass { Name = StandardTaxClassName, Rate = 0.2300m, IsDefault = true };
            var zero = new TaxClass { Name = ZeroTaxClassName, Rate = 0.0000m };
            var grocery = new Category { Name = GroceryCategoryName, SortOrder = 10 };

            db.TaxClasses.AddRange(standard, zero);
            db.Categories.Add(grocery);
            await db.SaveChangesAsync();

            // Needs Grocery's stamped id, so it cannot go in the batch above. The chain is
            // deliberate: the cycle tests need a real parent-child pair to reach for.
            var cheese = new Category
            {
                Name = CheeseCategoryName,
                ParentCategoryId = grocery.Id,
                SortOrder = 20,
            };

            db.Categories.Add(cheese);
            await db.SaveChangesAsync();

            var water = new Product
            {
                Sku = WaterSku,
                Name = WaterName,
                CategoryId = grocery.Id,
                TaxClassId = standard.Id,
                UnitPrice = (Money)1.2000m,
                CostPrice = (Money)WaterCostPrice,
            };

            var coffee = new Product
            {
                Sku = CoffeeSku,
                Name = CoffeeName,
                CategoryId = cheese.Id,
                TaxClassId = standard.Id,
                UnitPrice = (Money)4.5000m,
                CostPrice = (Money)CoffeeCostPrice,
            };

            // No category, no cost, no stock tracking. The awkward row: it is what proves a
            // response omits costPrice for a legitimately costless product too, and it is
            // the reason the margin tests must not point at it.
            var bag = new Product
            {
                Sku = BagSku,
                Name = BagName,
                TaxClassId = zero.Id,
                UnitPrice = (Money)0.1650m,
                TrackStock = false,
            };

            db.Products.AddRange(water, coffee, bag);
            await db.SaveChangesAsync();

            // Pass four: the dependents. Same reason as the pass above — a barcode or a stock
            // row added alongside its product would carry Guid.Empty in the foreign key.
            var waterCode = new Barcode { ProductId = water.Id, Code = WaterBarcode, IsPrimary = true };
            var waterMultipack = new Barcode { ProductId = water.Id, Code = WaterMultipackBarcode };
            var coffeeCode = new Barcode { ProductId = coffee.Id, Code = CoffeeBarcode, IsPrimary = true };

            db.Barcodes.AddRange(waterCode, waterMultipack, coffeeCode);

            db.StockItems.AddRange(
                new StockItem
                {
                    ProductId = water.Id,
                    OnHand = WaterOnHand,
                    ReorderPoint = WaterReorderPoint,
                },
                new StockItem
                {
                    ProductId = coffee.Id,
                    OnHand = CoffeeOnHand,
                    ReorderPoint = CoffeeReorderPoint,
                });

            await db.SaveChangesAsync();

            catalog = new SeededCatalog(
                standard.Id,
                zero.Id,
                grocery.Id,
                cheese.Id,
                water.Id,
                coffee.Id,
                bag.Id,
                waterCode.Id,
                waterMultipack.Id,
                coffeeCode.Id);
        });

        return catalog!;
    }
}
