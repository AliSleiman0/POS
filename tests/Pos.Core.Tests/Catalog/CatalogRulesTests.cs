using Pos.Core.Catalog;

namespace Pos.Core.Tests.Catalog;

/// <summary>
/// The numeric boundaries the catalog's write paths enforce before Postgres gets a chance to
/// round something.
/// </summary>
/// <remarks>
/// The scale cases are the ones that matter. Postgres does not refuse a fifth decimal
/// place — it rounds it and stores the result, so a price of 1.00005 becomes 1.0001 with no
/// error raised anywhere and the shop charges a fraction of a cent nobody typed. Every
/// assertion below with a five-decimal literal is about that silence.
/// </remarks>
public sealed class CatalogRulesTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("0.0000")]
    [InlineData("1.2000")]
    [InlineData("0.1650")]           // the seeded paper-bag price: a real 4th decimal
    [InlineData("1.5")]              // fewer decimals than the scale is fine
    [InlineData("999999999999999.9999")]
    public void An_amount_the_column_holds_exactly_is_storable(string value)
    {
        Assert.True(CatalogRules.IsStorableAmount(decimal.Parse(value, Culture)));
    }

    [Theory]
    [InlineData("-0.0001")]          // negative by the smallest possible amount
    [InlineData("-1")]
    [InlineData("1.00005")]          // scale 5: rounds to 1.0001 rather than failing
    [InlineData("0.00001")]
    [InlineData("1000000000000000")] // one digit too many before the point
    public void An_amount_the_column_would_change_or_reject_is_not_storable(string value)
    {
        Assert.False(CatalogRules.IsStorableAmount(decimal.Parse(value, Culture)));
    }

    [Fact]
    public void Trailing_zeros_do_not_make_an_amount_unstorable()
    {
        // 1.5m and 1.5000m are equal but carry different scales in their representation. A
        // client that serialises trailing zeros has done nothing wrong, so the check has to
        // compare values rather than read the scale off the decimal.
        Assert.True(CatalogRules.IsStorableAmount(1.5000m));
        Assert.True(CatalogRules.IsStorableAmount(1.5m));
    }

    [Theory]
    [InlineData("0")]                // zero-rated goods are a category, not an absence
    [InlineData("0.2300")]
    [InlineData("1")]                // degenerate but legal, and the constraint allows it
    public void A_rate_inside_the_check_constraint_is_valid(string value)
    {
        Assert.True(CatalogRules.IsValidTaxRate(decimal.Parse(value, Culture)));
    }

    [Theory]
    [InlineData("-0.0001")]
    [InlineData("1.0001")]
    [InlineData("20")]               // 20% typed as 20 — four hundred times too much
    [InlineData("0.20005")]          // scale 5 against a numeric(6,4) column
    public void A_rate_outside_the_check_constraint_is_not_valid(string value)
    {
        Assert.False(CatalogRules.IsValidTaxRate(decimal.Parse(value, Culture)));
    }

    [Fact]
    public void The_boundaries_are_inclusive_at_both_ends()
    {
        // Stated on its own because ck_tax_class_rate_range is `rate >= 0 AND rate <= 1`.
        // If these predicates were exclusive, a legitimate zero-rate class would be
        // rejected by the API and accepted by the database — the two layers disagreeing
        // about the same rule.
        Assert.True(CatalogRules.IsValidTaxRate(0m));
        Assert.True(CatalogRules.IsValidTaxRate(1m));
        Assert.True(CatalogRules.IsStorableAmount(0m));
        Assert.True(CatalogRules.IsStorableAmount(CatalogRules.MaxAmount));
    }

    private static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.InvariantCulture;
}
