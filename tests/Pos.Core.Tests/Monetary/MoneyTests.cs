using System.Globalization;
using Pos.Core.Monetary;

namespace Pos.Core.Tests.Monetary;

/// <summary>
/// The money type's arithmetic and, more importantly, the arithmetic it refuses to do.
/// </summary>
public sealed class MoneyTests
{
    [Fact]
    public void Zero_is_the_default_so_an_unassigned_amount_is_not_a_surprise()
    {
        // A struct field nobody assigns is `default`. If that were anything other than zero,
        // a forgotten assignment would be a wrong price rather than a free one — and free is
        // at least visible on a receipt.
        Assert.Equal(Money.Zero, default);
        Assert.True(Money.Zero.IsZero);
        Assert.Equal(0m, Money.Zero.ToDecimal());
    }

    [Fact]
    public void Money_equality_ignores_trailing_zeros()
    {
        // decimal.Equals compares values, not representations, and the hash codes agree.
        // This is what makes Assert.Equal usable in the fixtures: a price read back from
        // Postgres carries scale 4, and the literal in the test does not.
        Assert.Equal((Money)1.5m, (Money)1.5000m);
        Assert.Equal(((Money)1.5m).GetHashCode(), ((Money)1.5000m).GetHashCode());
    }

    [Fact]
    public void Arithmetic_is_exact_where_decimal_is_exact()
    {
        // 0.1 + 0.2 == 0.3 is false in binary floating point and true here. That single line
        // is why invariant 3 says decimal and never float or double: the error is small,
        // compounds across a basket, and surfaces as a receipt that does not add up.
        Assert.Equal((Money)0.3m, (Money)0.1m + (Money)0.2m);

        Assert.Equal((Money)1.5m, (Money)4m - (Money)2.5m);
        Assert.Equal((Money)(-2.5m), -(Money)2.5m);
        Assert.Equal((Money)7.5m, (Money)2.5m * 3m);
        Assert.Equal((Money)2.5m, (Money)7.5m / 3m);
    }

    [Fact]
    public void A_line_extension_keeps_every_decimal_place_it_needs()
    {
        // 0.3333 kg at 1.2340 is 0.41129220 — eight places, and none of them rounded away.
        // A Money that validated its scale on construction would have to round here, which
        // is precisely the per-line rounding the round-once rule forbids.
        var extension = (Money)1.2340m * 0.3333m;

        Assert.Equal(0.41129220m, extension.ToDecimal());
        Assert.False(extension.IsStorable);
        Assert.True(extension.RoundToStorage().IsStorable);
    }

    [Fact]
    public void Per_line_rounding_and_round_once_produce_different_totals()
    {
        // The bug this phase exists to prevent, demonstrated rather than asserted in prose.
        // Three lines that each land on a half-cent: rounding every line and summing gives
        // 1.02, carrying full precision and rounding the total gives 1.01. One cent, every
        // basket, and the Z-report never balances.
        Money[] lines = [(Money)0.335m, (Money)0.335m, (Money)0.335m];

        var roundedPerLine = Money.Sum(lines.Select(line => line.Round()));
        var roundedOnce = Money.Sum(lines).Round();

        Assert.Equal(1.02m, roundedPerLine.ToDecimal());
        Assert.Equal(1.01m, roundedOnce.ToDecimal());
        Assert.NotEqual(roundedPerLine, roundedOnce);
    }

    [Fact]
    public void Summing_an_empty_sequence_is_zero_rather_than_an_error()
    {
        // An empty cart is priced, not refused, by the engine. The endpoint rejects it as a
        // UI slip; the arithmetic has no opinion.
        Assert.Equal(Money.Zero, Money.Sum([]));
    }

    [Fact]
    public void Comparison_orders_amounts_including_across_zero()
    {
        Assert.True((Money)1m < (Money)2m);
        Assert.True((Money)2m >= (Money)2m);
        Assert.True((Money)(-1m) < Money.Zero);

        // Sorting matters: discount apportionment gives its remainder to the largest line,
        // and "largest" has to mean the same thing every time for the totals to be stable.
        Money[] amounts = [(Money)3m, (Money)(-1m), (Money)2m];
        Assert.Equal([(Money)(-1m), (Money)2m, (Money)3m], [.. amounts.Order()]);
    }

    [Fact]
    public void Rounding_a_total_is_a_separate_act_from_storing_a_line()
    {
        var value = (Money)1.23456m;

        Assert.Equal(1.23m, value.Round().ToDecimal());
        Assert.Equal(1.2346m, value.RoundToStorage().ToDecimal());
    }

    [Fact]
    public void A_ratio_of_two_amounts_is_a_share_and_not_an_amount()
    {
        // Money divided by money is dimensionless — it is what fraction of the cart a line
        // is — which is why it is a named method returning decimal rather than operator /.
        Assert.Equal(0.25m, Money.Ratio((Money)5m, (Money)20m));
    }

    [Fact]
    public void Money_does_not_add_to_a_bare_decimal()
    {
        // There is no operator +(Money, decimal), so `(Money)1m + 0.5m` does not compile.
        // The compiler is the assertion; this test records what it is asserting and pins the
        // deliberate escape hatch, so removing the explicit operators is a visible break.
        //
        // Uncommenting the next line must fail the build:
        //     var wrong = (Money)1m + 0.5m;
        Assert.Equal((Money)1.5m, (Money)1m + (Money)0.5m);
        Assert.Equal(1m, ((Money)1m).ToDecimal());
        Assert.Equal((Money)1m, Money.From(1m));
    }

    [Fact]
    public void Formatting_defaults_to_invariant_and_shows_the_minor_unit()
    {
        // Always at least the minor unit, and up to the storage scale when a line extension
        // genuinely needs it — trailing zeros past two places are noise, not information.
        Assert.Equal("1.20", ((Money)1.2m).ToString());
        Assert.Equal("1.234", ((Money)1.2340m).ToString());
        Assert.Equal("1.2345", ((Money)1.2345m).ToString());

        // A provider is honoured when one is given, and ignored when it is not. The default
        // has to be invariant rather than ambient: a decimal comma reaching a receipt total
        // or a log line is a support call, and InvariantGlobalization only makes the ambient
        // culture invariant on *this* build — it is not a guarantee to lean on.
        //
        // Built by hand rather than via GetCultureInfo("fr-FR"), which throws outright under
        // InvariantGlobalization: only the invariant culture exists in this process.
        var comma = new NumberFormatInfo { NumberDecimalSeparator = "," };

        Assert.Equal("1,20", ((Money)1.2m).ToString(null, comma));
        Assert.Equal("1.20", ((Money)1.2m).ToString(null, null));
    }

    [Fact]
    public void Comparing_to_something_that_is_not_money_throws_rather_than_ordering_it()
    {
        Assert.Throws<ArgumentException>(() => ((IComparable)(Money)1m).CompareTo("1"));
        Assert.Equal(1, ((IComparable)(Money)1m).CompareTo(null));
    }
}
