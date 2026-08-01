using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// The two tax modes, which are two meanings for a stored price rather than two ways of
/// showing one.
/// </summary>
public sealed class TaxCalculatorTests
{
    [Fact]
    public void Exclusive_tax_is_added_on_top_of_the_price()
    {
        Assert.Equal(2.30m, TaxCalculator.TaxOn((Money)10m, 0.23m, TaxMode.Exclusive).ToDecimal());
        Assert.Equal(10m, TaxCalculator.NetOfTax((Money)10m, 0.23m, TaxMode.Exclusive).ToDecimal());
    }

    [Fact]
    public void Inclusive_tax_is_extracted_from_within_the_price()
    {
        // gross × rate / (1 + rate). The common mistake is gross × rate, which on a 12.30
        // shelf price gives 2.829 instead of 2.30 — a 23% overstatement of the VAT owed, on
        // every line, in a figure that goes to a tax authority.
        Assert.Equal(2.30m, TaxCalculator.TaxOn((Money)12.30m, 0.23m, TaxMode.Inclusive).ToDecimal());
        Assert.Equal(10m, TaxCalculator.NetOfTax((Money)12.30m, 0.23m, TaxMode.Inclusive).ToDecimal());
    }

    [Fact]
    public void Net_plus_tax_returns_the_gross_in_inclusive_mode()
    {
        // The round trip that has to hold, or Subtotal and TaxTotal do not reconstitute the
        // total they were derived from.
        var gross = (Money)12.30m;

        Assert.Equal(
            gross,
            TaxCalculator.NetOfTax(gross, 0.23m, TaxMode.Inclusive)
            + TaxCalculator.TaxOn(gross, 0.23m, TaxMode.Inclusive));
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void A_zero_rate_produces_no_tax_in_either_mode(TaxMode mode)
    {
        // Zero-rated goods are a category, not an absence — bread and children's clothing —
        // so this is an ordinary rate rather than a special case.
        Assert.Equal(Money.Zero, TaxCalculator.TaxOn((Money)10m, 0m, mode));
        Assert.Equal((Money)10m, TaxCalculator.NetOfTax((Money)10m, 0m, mode));
    }

    [Fact]
    public void A_rate_of_one_halves_an_inclusive_price()
    {
        // Degenerate but legal: CatalogRules.IsValidTaxRate accepts 1. The inclusive divisor
        // is 2, so exactly half the shelf price is tax. Asserted so the boundary is a known
        // answer rather than an untested corner.
        Assert.Equal(5m, TaxCalculator.TaxOn((Money)10m, 1m, TaxMode.Inclusive).ToDecimal());
        Assert.Equal(10m, TaxCalculator.TaxOn((Money)10m, 1m, TaxMode.Exclusive).ToDecimal());
    }

    [Fact]
    public void Tax_is_not_rounded_here()
    {
        // 0.07 at 23% inclusive is 0.013089430894... The calculator returns it whole and the
        // pipeline rounds once at the header. Rounding here would be per-line rounding by
        // another name.
        var tax = TaxCalculator.TaxOn((Money)0.07m, 0.23m, TaxMode.Inclusive);

        Assert.False(tax.IsStorable);
        Assert.True(tax.ToDecimal() > 0.0130m && tax.ToDecimal() < 0.0131m);
    }

    [Fact]
    public void An_unknown_mode_throws_rather_than_picking_one()
    {
        // A default arm that silently chose Exclusive would misprice an entire tenant's
        // catalog, and every total would look plausible.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TaxCalculator.TaxOn((Money)10m, 0.23m, (TaxMode)99));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => TaxCalculator.NetOfTax((Money)10m, 0.23m, (TaxMode)99));
    }
}
