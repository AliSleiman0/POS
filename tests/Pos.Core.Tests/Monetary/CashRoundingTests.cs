using System.Globalization;
using Pos.Core.Monetary;

namespace Pos.Core.Tests.Monetary;

/// <summary>
/// Rounding a payable total to the smallest coin that exists, and recording the difference.
/// </summary>
/// <remarks>
/// The adjustment is the point. A till that quietly nudged €4.97 to €4.95 would be 2c short
/// at close with nothing in the system to explain it, and the owner checking the variance
/// every morning would be chasing a bug that is actually a rounding rule.
/// </remarks>
public sealed class CashRoundingTests
{
    [Theory]
    [InlineData("4.97", "4.95", "-0.02")]
    [InlineData("4.98", "5.00", "0.02")]
    [InlineData("4.95", "4.95", "0")]      // already on the increment: no adjustment
    [InlineData("0.02", "0.00", "-0.02")]  // a total smaller than the coin rounds to nothing
    [InlineData("0.03", "0.05", "0.02")]
    public void Cash_rounding_to_five_cents_produces_a_recorded_adjustment(
        string total, string expected, string adjustment)
    {
        var amount = (Money)decimal.Parse(total, Culture);

        var rounded = CashRounding.ToIncrement(amount, 0.05m);

        Assert.Equal(decimal.Parse(expected, Culture), rounded.ToDecimal());
        Assert.Equal(
            decimal.Parse(adjustment, Culture),
            CashRounding.AdjustmentFor(amount, 0.05m).ToDecimal());

        // The identity the sale row has to satisfy: what is stored as RoundingAdjustment is
        // exactly what closes the gap between the computed total and the amount taken.
        Assert.Equal(rounded, amount + CashRounding.AdjustmentFor(amount, 0.05m));
    }

    [Theory]
    [InlineData("2.525")]   // a midpoint against the 0.05 increment
    [InlineData("2.575")]
    public void A_total_exactly_between_two_coins_rounds_away_from_zero(string total)
    {
        // 2.525 / 0.05 is 50.5, and the midpoint has to go the same way the rest of the
        // system does or the two rounding rules disagree on the one value they share.
        var amount = (Money)decimal.Parse(total, Culture);
        var rounded = CashRounding.ToIncrement(amount, 0.05m);

        Assert.Equal(Rounding.To(amount.ToDecimal() / 0.05m, 0) * 0.05m, rounded.ToDecimal());
    }

    [Fact]
    public void A_negative_total_rounds_away_from_zero_as_well()
    {
        // Refunds are negative totals. -4.97 must round to -4.95, matching the sale it
        // reverses, or refunding a cash-rounded sale is off by 4c.
        Assert.Equal(-4.95m, CashRounding.ToIncrement((Money)(-4.97m), 0.05m).ToDecimal());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-0.05")]
    public void Cash_rounding_with_a_zero_increment_changes_nothing(string increment)
    {
        // Zero is the default and the common case, so it must be an exact no-op rather than
        // a divide by zero. A negative increment is refused by the column check constraint
        // and treated as "no rule" here rather than inverting the direction.
        var amount = (Money)4.97m;

        Assert.Equal(amount, CashRounding.ToIncrement(amount, decimal.Parse(increment, Culture)));
        Assert.Equal(Money.Zero, CashRounding.AdjustmentFor(amount, decimal.Parse(increment, Culture)));
    }

    [Fact]
    public void An_increment_finer_than_the_storage_scale_still_produces_a_storable_total()
    {
        // steps × increment can carry more decimal places than either operand. The result
        // has to survive numeric(19,4) or the total stored is not the total computed.
        var rounded = CashRounding.ToIncrement((Money)1.00013m, 0.00025m);

        Assert.True(rounded.IsStorable);
    }

    private static CultureInfo Culture => CultureInfo.InvariantCulture;
}
