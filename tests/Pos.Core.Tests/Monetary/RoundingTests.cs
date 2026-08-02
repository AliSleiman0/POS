using System.Globalization;
using Pos.Core.Monetary;

namespace Pos.Core.Tests.Monetary;

/// <summary>
/// The rounding contract, stated as tests because it is the one rule in the system a
/// customer can dispute at a counter.
/// </summary>
/// <remarks>
/// Every midpoint case below returns a different answer under .NET's <i>default</i>
/// rounding, which is banker's: <c>decimal.Round(2.5m, 0)</c> is 2, and <c>0.125m</c> to two
/// places is <c>0.12</c>. So each of these assertions fails if someone drops the
/// <see cref="Rounding.Mode"/> argument from a <c>decimal.Round</c> call — which is exactly
/// the edit that would otherwise ship silently, because the totals stay plausible.
/// </remarks>
public sealed class RoundingTests
{
    [Theory]
    [InlineData("0.005", "0.01")]
    [InlineData("0.015", "0.02")]   // banker's gives 0.02 here too — kept for the pair below
    [InlineData("0.025", "0.03")]   // banker's gives 0.02: the case that catches the default
    [InlineData("0.045", "0.05")]   // banker's gives 0.04
    [InlineData("2.345", "2.35")]
    public void Rounding_is_away_from_zero_at_the_boundaries(string value, string expected)
    {
        Assert.Equal(
            decimal.Parse(expected, Culture),
            Rounding.To(decimal.Parse(value, Culture), Rounding.DisplayScale));
    }

    [Theory]
    [InlineData("-0.005", "-0.01")]
    [InlineData("-0.025", "-0.03")]
    [InlineData("-2.345", "-2.35")]
    public void A_negative_midpoint_rounds_away_from_zero_too(string value, string expected)
    {
        // "Away from zero", not "up". A refund of -0.025 becomes -0.03, so refunding a sale
        // returns exactly what the sale charged rather than a cent less.
        Assert.Equal(
            decimal.Parse(expected, Culture),
            Rounding.To(decimal.Parse(value, Culture), Rounding.DisplayScale));
    }

    [Fact]
    public void The_mode_is_away_from_zero_and_not_the_platform_default()
    {
        // Stated directly, so the constant cannot be changed without a test naming it. The
        // second assertion is what makes the first meaningful: it shows the two modes
        // genuinely disagree on a value this system will see.
        Assert.Equal(MidpointRounding.AwayFromZero, Rounding.Mode);
        Assert.NotEqual(
            decimal.Round(0.025m, 2, MidpointRounding.ToEven),
            Rounding.To(0.025m, 2));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.5")]                      // fewer places than the scale is fine
    [InlineData("1.5000")]                   // trailing zeros are a representation, not a value
    [InlineData("0.1650")]
    [InlineData("999999999999999.9999")]
    [InlineData("-999999999999999.9999")]
    public void A_value_the_column_holds_exactly_is_storable(string value)
    {
        Assert.True(Rounding.IsStorable(decimal.Parse(value, Culture)));
    }

    [Theory]
    [InlineData("1.00005")]                  // scale 5: Postgres rounds it to 1.0001 silently
    [InlineData("-1.00005")]
    [InlineData("0.00001")]
    [InlineData("1000000000000000")]         // one digit too many before the point
    [InlineData("-1000000000000000")]
    public void A_value_the_column_would_change_or_reject_is_not_storable(string value)
    {
        Assert.False(Rounding.IsStorable(decimal.Parse(value, Culture)));
    }

    [Fact]
    public void The_storage_scale_is_wider_than_the_scale_a_person_pays_at()
    {
        // Not a tautology: it is the whole reason a line extension can be carried exactly
        // while the total is still expressible in coins. If these were equal, the round-once
        // rule would have nothing to protect.
        Assert.True(Rounding.StorageScale > Rounding.DisplayScale);
    }

    private static CultureInfo Culture => CultureInfo.InvariantCulture;
}
