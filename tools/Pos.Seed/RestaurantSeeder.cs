using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Tenancy;
using Pos.Data;

namespace Pos.Seed;

/// <summary>What one restaurant run left behind, for the summary the tool prints.</summary>
internal sealed record RestaurantSummary(
    int Stations,
    int Areas,
    int Tables,
    int MenuItems,
    int ModifierGroups)
{
    public static readonly RestaurantSummary Skipped = new(0, 0, 0, 0, 0);
}

/// <summary>
/// Turns the seeded shop into a restaurant that can actually be worked.
/// </summary>
/// <remarks>
/// <b>This exists because a phase with no way in is a phase nobody has looked at.</b> The whole
/// server side of an order's life shipped before any of it had a screen, and without this flag
/// there was no way to seat a table or fire a round by hand either — so the only evidence the
/// kitchen worked was its own tests. A room, a menu and a set of stations from one command is
/// what makes the next session's UI something to check rather than something to imagine.
/// <para>
/// <b>The menu is small and every row earns its place.</b> A required modifier group so the
/// refusal can be seen; a category chain so routing has to walk it; one product routed against
/// its category so the override is exercised; and two areas, because a floor screen that has only
/// ever been shown one grouping is a floor screen with a latent bug.
/// </para>
/// <para>
/// Idempotent by natural key within the tenant — station name, table name, SKU, group name — on
/// the same terms as <see cref="CatalogSeeder"/>. Anything already there is left exactly as it
/// is, because this is run repeatedly against a database somebody is mid-way through testing in.
/// </para>
/// </remarks>
internal sealed class RestaurantSeeder(IServiceProvider services)
{
    private const string Bar = "Bar";
    private const string Grill = "Grill";
    private const string Pass = "Pass";

    /// <summary>
    /// The menu's categories, and where each one sends its food.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not routed uniformly.</b> "Food" points at the pass and "Starters" and
    /// "Desserts" set nothing, so those two only reach a station by the walk up the chain —
    /// which is the part of <c>StationRouting</c> a flat menu would never exercise. "Mains"
    /// overrides its parent and goes to the grill.
    /// </remarks>
    private static readonly MenuCategory[] Categories =
    [
        new("Food", Parent: null, Station: Pass, SortOrder: 10),
        new("Starters", Parent: "Food", Station: null, SortOrder: 11),
        new("Mains", Parent: "Food", Station: Grill, SortOrder: 12),
        new("Desserts", Parent: "Food", Station: null, SortOrder: 13),
        new("Drinks", Parent: null, Station: Bar, SortOrder: 20),
    ];

    private static readonly MenuItem[] Menu =
    [
        new("MENU-101", "Soup of the Day", "Starters", 6.5000m, 1.8000m),
        new("MENU-102", "Garlic Bread", "Starters", 5.0000m, 0.9000m),

        new("MENU-201", "Beef Burger", "Mains", 16.5000m, 5.2000m),
        new("MENU-202", "Fish and Chips", "Mains", 18.0000m, 6.1000m),
        new("MENU-203", "Mushroom Risotto", "Mains", 15.5000m, 3.4000m),

        new("MENU-301", "Sticky Toffee Pudding", "Desserts", 7.5000m, 1.6000m),

        new("MENU-401", "House Red, glass", "Drinks", 7.5000m, 2.1000m),
        new("MENU-402", "Sparkling Water", "Drinks", 3.0000m, 0.4000m),
        new("MENU-403", "Coffee", "Drinks", 3.2000m, 0.3000m),
    ];

    /// <summary>
    /// The modifiers, which are ordinary products carrying <c>IsModifier</c>.
    /// </summary>
    /// <remarks>
    /// No category, deliberately: a modifier is never the thing that routes, because it rides
    /// its parent onto that item's ticket as a line of text. It is also what keeps them out of
    /// the register grid and out of barcode search — nobody scans "extra cheese".
    /// </remarks>
    private static readonly MenuItem[] Modifiers =
    [
        new("MOD-001", "Rare", null, 0.0000m, null),
        new("MOD-002", "Medium", null, 0.0000m, null),
        new("MOD-003", "Well done", null, 0.0000m, null),
        new("MOD-010", "Extra cheese", null, 1.5000m, 0.3000m),
        new("MOD-011", "Streaky bacon", null, 2.0000m, 0.7000m),
        new("MOD-012", "No onion", null, 0.0000m, null),
    ];

    private static readonly ModifierGroupSeed[] Groups =
    [
        // Required, which is the one a person should try to skip: the API refuses the line and
        // names the question, and the till's own gating is a courtesy on top of that.
        new("Cooked how?", Min: 1, Max: 1, SortOrder: 10,
            Options: ["MOD-001", "MOD-002", "MOD-003"],
            AppliesTo: ["MENU-201"]),

        new("Extras", Min: 0, Max: null, SortOrder: 20,
            Options: ["MOD-010", "MOD-011", "MOD-012"],
            AppliesTo: ["MENU-201", "MENU-202"]),
    ];

    private static readonly AreaSeed[] Areas =
    [
        new("Main room", 10, ["1", "2", "3", "4", "5", "6"], Seats: 4),
        new("Terrace", 20, ["T1", "T2"], Seats: 2),
    ];

    public async Task<RestaurantSummary> RunAsync(Guid tenantId)
    {
        await using var scope = services.CreateAsyncScope();

        // The ordinary application path, as pos_app — so row-level security applies to every
        // insert below. If a policy rejected these writes the seed would fail here rather than
        // producing rows the API cannot read.
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        await SetServiceModeAsync(db, tenantId);

        var stations = new Dictionary<string, Guid>(StringComparer.Ordinal);

        foreach (var (name, sortOrder) in new[] { (Bar, 10), (Grill, 20), (Pass, 30) })
        {
            stations[name] = await EnsureStationAsync(db, name, sortOrder);
        }

        await EnsureCategoriesAsync(db, stations);
        await EnsureAreasAsync(db);

        var taxClass = await db.TaxClasses.FirstAsync(t => t.IsDefault);

        foreach (var item in Menu)
        {
            await EnsureMenuItemAsync(db, item, taxClass.Id, isModifier: false);
        }

        foreach (var modifier in Modifiers)
        {
            await EnsureMenuItemAsync(db, modifier, taxClass.Id, isModifier: true);
        }

        foreach (var group in Groups)
        {
            await EnsureGroupAsync(db, group);
        }

        return new RestaurantSummary(
            await db.Stations.CountAsync(),
            await db.ServiceAreas.CountAsync(),
            await db.DiningTables.CountAsync(),
            await db.Products.CountAsync(p => !p.IsModifier),
            await db.ModifierGroups.CountAsync());
    }

    /// <summary>
    /// Switches the shop into restaurant mode, on an existing tenant as well as a new one.
    /// </summary>
    /// <remarks>
    /// One of the few things this tool changes about a shop that already exists, and for the
    /// reason <c>--cash-rounding</c> is: a flag nobody typed by accident is a deliberate act, and
    /// a mode that only applied to a tenant which did not exist yet would be useless to the
    /// person who has been testing against <c>corner-shop</c> all week.
    /// <para>
    /// Unlike a tax mode, this is reversible — <c>PUT /settings</c> can put it back, provided no
    /// order is still open.
    /// </para>
    /// </remarks>
    private static async Task SetServiceModeAsync(AppDbContext db, Guid tenantId)
    {
        // Filtered by hand: `tenant` is the tenant list, so it carries no query filter.
        var tenant = await db.Tenants.FirstAsync(t => t.Id == tenantId);

        if (tenant.ServiceMode == ServiceMode.Restaurant)
        {
            return;
        }

        tenant.ServiceMode = ServiceMode.Restaurant;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> EnsureStationAsync(AppDbContext db, string name, int sortOrder)
    {
        var existing = await db.Stations.FirstOrDefaultAsync(s => s.Name == name);

        if (existing is not null)
        {
            return existing.Id;
        }

        var station = new Station { Name = name, SortOrder = sortOrder };

        db.Stations.Add(station);
        await db.SaveChangesAsync();

        return station.Id;
    }

    /// <summary>
    /// Writes the menu's categories, parents before children, and points them at stations.
    /// </summary>
    /// <remarks>
    /// The routing is applied to categories that already exist too. A shop seeded before this
    /// flag existed has "Food" with no station on it, and leaving it that way would mean the
    /// first fire of the evening refused itself for a reason the person could not see.
    /// </remarks>
    private static async Task EnsureCategoriesAsync(AppDbContext db, Dictionary<string, Guid> stations)
    {
        foreach (var seed in Categories)
        {
            var parentId = seed.Parent is null
                ? (Guid?)null
                : (await db.Categories.FirstAsync(c => c.Name == seed.Parent)).Id;

            var stationId = seed.Station is null ? (Guid?)null : stations[seed.Station];

            var existing = await db.Categories.FirstOrDefaultAsync(c => c.Name == seed.Name);

            if (existing is not null)
            {
                if (existing.StationId != stationId)
                {
                    existing.StationId = stationId;
                    await db.SaveChangesAsync();
                }

                continue;
            }

            db.Categories.Add(new Category
            {
                Name = seed.Name,
                ParentCategoryId = parentId,
                SortOrder = seed.SortOrder,
                StationId = stationId,
            });

            // Saved one at a time because a child needs its parent's stamped id: there are no
            // navigation properties, so EF does no foreign-key fixup. Same as CatalogSeeder.
            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsureAreasAsync(AppDbContext db)
    {
        foreach (var seed in Areas)
        {
            var area = await db.ServiceAreas.FirstOrDefaultAsync(a => a.Name == seed.Name);

            if (area is null)
            {
                area = new ServiceArea { Name = seed.Name, SortOrder = seed.SortOrder };

                db.ServiceAreas.Add(area);
                await db.SaveChangesAsync();
            }

            for (var index = 0; index < seed.Tables.Length; index++)
            {
                var name = seed.Tables[index];

                if (await db.DiningTables.AnyAsync(t => t.Name == name))
                {
                    continue;
                }

                db.DiningTables.Add(new DiningTable
                {
                    ServiceAreaId = area.Id,
                    Name = name,
                    Seats = seed.Seats,
                    SortOrder = (index + 1) * 10,
                });
            }

            await db.SaveChangesAsync();
        }
    }

    private static async Task EnsureMenuItemAsync(
        AppDbContext db,
        MenuItem seed,
        Guid taxClassId,
        bool isModifier)
    {
        var sku = Product.NormalizeSku(seed.Sku)!;

        if (await db.Products.AnyAsync(p => p.Sku == sku))
        {
            return;
        }

        var categoryId = seed.CategoryName is null
            ? (Guid?)null
            : (await db.Categories.FirstAsync(c => c.Name == seed.CategoryName)).Id;

        db.Products.Add(new Product
        {
            Sku = sku,
            Name = seed.Name,
            CategoryId = categoryId,
            TaxClassId = taxClassId,
            UnitPrice = (Money)seed.UnitPrice,
            CostPrice = (Money?)seed.CostPrice,
            Unit = Unit.Each,

            // No stock on a menu: a kitchen's inventory is ingredients, not dishes, and a
            // burger that decremented "burgers on hand" would be a number nobody maintains.
            // Phase 10's rule 4 moves stock at payment for the things that do track it.
            TrackStock = false,
            IsModifier = isModifier,
        });

        await db.SaveChangesAsync();
    }

    private static async Task EnsureGroupAsync(AppDbContext db, ModifierGroupSeed seed)
    {
        var group = await db.ModifierGroups.FirstOrDefaultAsync(g => g.Name == seed.Name);

        if (group is null)
        {
            group = new ModifierGroup
            {
                Name = seed.Name,
                MinSelections = seed.Min,
                MaxSelections = seed.Max,
                SortOrder = seed.SortOrder,
            };

            db.ModifierGroups.Add(group);
            await db.SaveChangesAsync();
        }

        for (var index = 0; index < seed.Options.Length; index++)
        {
            var productId = await SkuIdAsync(db, seed.Options[index]);

            if (!await db.ModifierOptions.AnyAsync(o => o.ModifierGroupId == group.Id && o.ProductId == productId))
            {
                db.ModifierOptions.Add(new ModifierOption
                {
                    ModifierGroupId = group.Id,
                    ProductId = productId,
                    SortOrder = (index + 1) * 10,

                    // The first option is what the sheet pre-selects. Advisory, and unconstrained
                    // by design — see ModifierOption.IsDefault.
                    IsDefault = index == 0,
                });
            }
        }

        foreach (var sku in seed.AppliesTo)
        {
            var productId = await SkuIdAsync(db, sku);

            if (!await db.ProductModifierGroups.AnyAsync(
                p => p.ProductId == productId && p.ModifierGroupId == group.Id))
            {
                db.ProductModifierGroups.Add(new ProductModifierGroup
                {
                    ProductId = productId,
                    ModifierGroupId = group.Id,
                    SortOrder = seed.SortOrder,
                });
            }
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> SkuIdAsync(AppDbContext db, string sku)
    {
        var normalised = Product.NormalizeSku(sku)!;

        return (await db.Products.FirstAsync(p => p.Sku == normalised)).Id;
    }

    private sealed record MenuCategory(string Name, string? Parent, string? Station, int SortOrder);

    private sealed record MenuItem(
        string Sku,
        string Name,
        string? CategoryName,
        decimal UnitPrice,
        decimal? CostPrice);

    private sealed record ModifierGroupSeed(
        string Name,
        int Min,
        int? Max,
        int SortOrder,
        string[] Options,
        string[] AppliesTo);

    private sealed record AreaSeed(string Name, int SortOrder, string[] Tables, int Seats);
}
