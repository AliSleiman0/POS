using Pos.Core.Entities;

namespace Pos.Core.Tests.Entities;

/// <summary>
/// The catalog entities carry almost no behaviour — 2.1 is data shape. What they do carry
/// is a set of defaults that decide what happens to a row nobody configured, and those are
/// worth pinning because the failure mode is silent: a service item that quietly tracks
/// stock, or a weighed product that cannot take a fractional quantity.
/// </summary>
public sealed class CatalogEntityTests
{
    [Fact]
    public void A_new_product_is_active_and_tracks_stock()
    {
        var product = NewProduct();

        Assert.True(product.IsActive);

        // Tracking by default is the safe direction: a product that tracks stock when it
        // should not accumulates a number nobody reads, whereas one that does not track
        // when it should silently stops decrementing and the shop finds out at stocktake.
        Assert.True(product.TrackStock);
    }

    [Fact]
    public void A_new_product_is_sold_by_the_each_unit()
    {
        Assert.Equal(Unit.Each, NewProduct().Unit);

        // The C# initialiser and the enum's zero value have to agree. A row written by an
        // import or a raw INSERT gets the zero value with no initialiser involved, and
        // "Each" is the only member where that is harmless.
        Assert.Equal(Unit.Each, default);
    }

    [Fact]
    public void The_unit_names_are_the_ones_the_check_constraint_allows()
    {
        // ck_product_unit_allowed is generated from these names by HasEnumAsText. Renaming
        // a member rewrites the constraint in the next migration and orphans every row
        // already written with the old name — a data problem, presenting as a check
        // constraint violation on an unrelated update months later.
        Assert.Equal(["Each", "Kilogram", "Litre"], Enum.GetNames<Unit>());
    }

    [Fact]
    public void A_new_category_is_active_and_top_level()
    {
        var category = new Category { Name = "Grocery" };

        Assert.True(category.IsActive);
        Assert.Null(category.ParentCategoryId);
    }

    [Fact]
    public void A_new_tax_class_is_not_the_default()
    {
        // At most one tax class per tenant may be the default, enforced by a filtered
        // unique index. If new instances defaulted to true, creating a second tax class
        // would fail on the index rather than on anything the user did.
        Assert.False(new TaxClass { Name = "Standard" }.IsDefault);
    }

    [Fact]
    public void A_stock_item_with_no_reorder_point_never_needs_reordering()
    {
        var stock = new StockItem { ProductId = Guid.CreateVersion7(), OnHand = 0m };

        Assert.Null(stock.ReorderPoint);
        Assert.False(stock.IsBelowReorderPoint);
    }

    [Theory]
    [InlineData(11, 10, false)]
    [InlineData(10, 10, true)]
    [InlineData(9, 10, true)]
    [InlineData(-2, 10, true)]
    public void A_stock_item_is_below_its_reorder_point_at_the_point_itself(
        decimal onHand,
        decimal reorderPoint,
        bool expected)
    {
        // The boundary is the whole content of this property. "Order more when you are
        // down to 10" means 10 counts, and an exclusive comparison would delay every
        // reorder by one unit sold.
        var stock = new StockItem
        {
            ProductId = Guid.CreateVersion7(),
            OnHand = onHand,
            ReorderPoint = reorderPoint,
        };

        Assert.Equal(expected, stock.IsBelowReorderPoint);
    }

    [Theory]
    [InlineData("SKU-1001", "SKU-1001")]
    [InlineData("sku-1001", "SKU-1001")]
    [InlineData("  sku-1001  ", "SKU-1001")]
    [InlineData("Sku-1001", "SKU-1001")]
    public void Normalize_sku_canonicalises_staff_input(string input, string expected)
    {
        // The uniqueness index is a plain unique index on the stored value, so it is only
        // as case-insensitive as what is written into it. Without this, "abc" and "ABC"
        // are two products and the second one is a duplicate nobody can see.
        Assert.Equal(expected, Product.NormalizeSku(input));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Normalize_sku_returns_null_when_nothing_usable_remains(string? input)
    {
        // Null, not empty: an empty SKU would be storable and would then collide with the
        // next empty one on the unique index, reported as a duplicate-SKU error for a
        // field the user left blank.
        Assert.Null(Product.NormalizeSku(input));
    }

    [Fact]
    public void Normalize_sku_truncates_to_the_stored_length()
    {
        var normalised = Product.NormalizeSku(new string('a', Product.SkuMaxLength + 20));

        Assert.NotNull(normalised);
        Assert.Equal(Product.SkuMaxLength, normalised.Length);
    }

    private static Product NewProduct() => new()
    {
        Sku = "SKU-1001",
        Name = "Still Water 500ml",
        TaxClassId = Guid.CreateVersion7(),
        UnitPrice = 1.20m,
    };
}
