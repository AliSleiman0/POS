using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data;

namespace Pos.Seed;

/// <summary>What one run left behind, for the summary the tool prints.</summary>
internal sealed record CatalogSummary(int TaxClasses, int Categories, int Products, int Barcodes, int StockItems)
{
    public static readonly CatalogSummary Skipped = new(0, 0, 0, 0, 0);
}

/// <summary>
/// A small catalog for the seeded tenant: a couple of tax classes, three categories, six
/// products, their barcodes and their stock rows.
/// </summary>
/// <remarks>
/// Small, but not arbitrary. Every row here exists to make something checkable that would
/// otherwise need an endpoint that does not exist yet — a product with two barcodes, one
/// sold by weight, one priced to four decimals, one with no category that does not track
/// stock, and one sitting below its reorder point.
/// <para>
/// <b>Written level by level, and that is required.</b> There are no navigation properties,
/// so EF has no relationship to fix up: a product's <c>Id</c> is <c>Guid.Empty</c> until
/// <c>TenantSaveChangesInterceptor</c> stamps it during <c>SaveChanges</c>. Adding a product
/// and its barcode in one call would write <c>ProductId = Guid.Empty</c> and fail the
/// foreign key.
/// </para>
/// <para>
/// Idempotent by natural key within the tenant — SKU, barcode, name. Anything already there
/// is left exactly as it is, including its price: this is run repeatedly against a database
/// somebody is in the middle of testing with.
/// </para>
/// </remarks>
internal sealed class CatalogSeeder(IServiceProvider services)
{
    /// <summary>The catalog, as data rather than as a run of imperative calls.</summary>
    private static readonly ProductSeed[] Products =
    [
        new("SKU-1001", "Still Water 500ml", "Grocery", "Standard", 1.2000m, 0.5500m, Unit.Each,
            TrackStock: true, OnHand: 48m, ReorderPoint: 12m,
            // Two codes for one product: the multipack scans differently from the single.
            // One barcode per product is the modelling mistake this exists to disprove.
            Barcodes: ["5099999000011", "5099999000028"]),

        new("SKU-1002", "White Sliced Pan", "Grocery", "Zero", 2.1000m, null, Unit.Each,
            // Below its reorder point on purpose, so 2.4's low-stock filter has a hit.
            TrackStock: true, OnHand: 6m, ReorderPoint: 10m,
            Barcodes: ["5099999000035"]),

        new("SKU-1003", "Irish Cheddar", "Cheese", "Standard", 12.9500m, 7.4000m, Unit.Kilogram,
            // Sold by weight, and the on-hand is fractional because 4.35 kg of cheese is an
            // ordinary amount of cheese.
            TrackStock: true, OnHand: 4.3500m, ReorderPoint: 2m,
            Barcodes: ["2000000000015"]),

        new("SKU-1004", "Olive Oil 1L", "Grocery", "Standard", 8.9500m, 5.2000m, Unit.Litre,
            TrackStock: true, OnHand: 14m, ReorderPoint: null,
            Barcodes: ["5099999000042"]),

        // The fourth decimal docs/DATA-MODEL.md exists for, visible in the database rather
        // than argued about: a bag costs 16.5 cents, and 0.17 or 0.16 are both wrong.
        new("SKU-1005", "Paper Bag", "Grocery", "Standard", 0.1650m, 0.0900m, Unit.Each,
            TrackStock: true, OnHand: 500m, ReorderPoint: 100m,
            Barcodes: ["5099999000059"]),

        // No category, no barcode, no stock row: a service item. Also the row that proves
        // TrackStock = false survives the insert rather than being turned into true by the
        // column default.
        new("SKU-1006", "Coffee to Go", null, "Standard", 3.5000m, null, Unit.Each,
            TrackStock: false, OnHand: null, ReorderPoint: null,
            Barcodes: []),
    ];

    public async Task<CatalogSummary> RunAsync(Guid tenantId)
    {
        await using var scope = services.CreateAsyncScope();

        // The ordinary application path: resolve the tenant, then write as the application
        // does. Connected as pos_app, so row-level security applies to every insert below —
        // if a policy rejected these writes, the seed would fail here rather than producing
        // rows the API cannot read.
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await EnsureTaxClassAsync(db, "Standard", 0.2300m, isDefault: true);
        await EnsureTaxClassAsync(db, "Zero", 0.0000m, isDefault: false);

        await EnsureCategoryAsync(db, "Grocery", parent: null, sortOrder: 10);
        var deli = await EnsureCategoryAsync(db, "Deli", parent: null, sortOrder: 20);

        // A child category, so the self-referencing tenant-scoped foreign key is exercised
        // by the seed and not only by a test.
        await EnsureCategoryAsync(db, "Cheese", parent: deli, sortOrder: 10);

        foreach (var seed in Products)
        {
            await EnsureProductAsync(db, seed);
        }

        // Counted after the fact rather than tallied as we go: these are the numbers a
        // person would get by querying, which is what the summary should report.
        return new CatalogSummary(
            await db.TaxClasses.CountAsync(),
            await db.Categories.CountAsync(),
            await db.Products.CountAsync(),
            await db.Barcodes.CountAsync(),
            await db.StockItems.CountAsync());
    }

    private static async Task<TaxClass> EnsureTaxClassAsync(
        AppDbContext db,
        string name,
        decimal rate,
        bool isDefault)
    {
        var existing = await db.TaxClasses.FirstOrDefaultAsync(t => t.Name == name);

        if (existing is not null)
        {
            return existing;
        }

        var taxClass = new TaxClass { Name = name, Rate = rate, IsDefault = isDefault };

        db.TaxClasses.Add(taxClass);
        await db.SaveChangesAsync();

        return taxClass;
    }

    private static async Task<Category> EnsureCategoryAsync(
        AppDbContext db,
        string name,
        Category? parent,
        int sortOrder)
    {
        var existing = await db.Categories.FirstOrDefaultAsync(c => c.Name == name);

        if (existing is not null)
        {
            return existing;
        }

        var category = new Category
        {
            Name = name,
            ParentCategoryId = parent?.Id,
            SortOrder = sortOrder,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync();

        return category;
    }

    private static async Task EnsureProductAsync(AppDbContext db, ProductSeed seed)
    {
        var sku = Product.NormalizeSku(seed.Sku)!;
        var product = await db.Products.FirstOrDefaultAsync(p => p.Sku == sku);

        if (product is null)
        {
            var taxClass = await db.TaxClasses.FirstAsync(t => t.Name == seed.TaxClassName);

            var category = seed.CategoryName is null
                ? null
                : await db.Categories.FirstAsync(c => c.Name == seed.CategoryName);

            product = new Product
            {
                Sku = sku,
                Name = seed.Name,
                CategoryId = category?.Id,
                TaxClassId = taxClass.Id,
                UnitPrice = seed.UnitPrice,
                CostPrice = seed.CostPrice,
                Unit = seed.Unit,
                TrackStock = seed.TrackStock,
            };

            db.Products.Add(product);

            // Saved before the barcodes and the stock row below, which need its id.
            await db.SaveChangesAsync();
        }

        foreach (var code in seed.Barcodes)
        {
            if (!await db.Barcodes.AnyAsync(b => b.Code == code))
            {
                db.Barcodes.Add(new Barcode
                {
                    ProductId = product.Id,
                    Code = code,
                    IsPrimary = code == seed.Barcodes[0],
                });
            }
        }

        if (seed.OnHand is { } onHand && !await db.StockItems.AnyAsync(s => s.ProductId == product.Id))
        {
            db.StockItems.Add(new StockItem
            {
                ProductId = product.Id,
                OnHand = onHand,
                ReorderPoint = seed.ReorderPoint,
            });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>One product and everything that hangs off it.</summary>
    private sealed record ProductSeed(
        string Sku,
        string Name,
        string? CategoryName,
        string TaxClassName,
        decimal UnitPrice,
        decimal? CostPrice,
        Unit Unit,
        bool TrackStock,
        decimal? OnHand,
        decimal? ReorderPoint,
        string[] Barcodes);
}
