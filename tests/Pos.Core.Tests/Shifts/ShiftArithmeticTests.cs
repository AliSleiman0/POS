using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Shifts;

namespace Pos.Core.Tests.Shifts;

/// <summary>
/// What should be in the drawer, and how far off it was.
/// </summary>
public sealed class ShiftArithmeticTests
{
    [Fact]
    public void Expected_cash_is_the_float_plus_cash_sales_minus_refunds_drops_and_payouts()
    {
        // A worked day: €100 float, €248.75 taken in cash, one €12.30 refund paid out, and a
        // €200 drop to the safe. Refund tenders are negative, so they subtract with no special
        // case; the drop is negative for the same reason.
        var expected = ShiftArithmetic.ExpectedCash(
            openingFloat: (Money)100m,
            netCashTendered: (Money)248.75m - (Money)12.30m,
            cashMovements: (Money)(-200m));

        Assert.Equal(136.45m, expected.ToDecimal());
    }

    [Fact]
    public void A_variance_is_counted_minus_expected()
    {
        // Negative means short, which is the direction that starts a conversation.
        Assert.Equal(1.25m, ShiftArithmetic.Variance((Money)150m, (Money)148.75m).ToDecimal());
        Assert.Equal(-5m, ShiftArithmetic.Variance((Money)145m, (Money)150m).ToDecimal());
        Assert.True(ShiftArithmetic.Variance((Money)150m, (Money)150m).IsZero);
    }

    [Fact]
    public void A_shift_that_sold_nothing_expects_its_float_back()
    {
        Assert.Equal(
            100m,
            ShiftArithmetic.ExpectedCash((Money)100m, Money.Zero, Money.Zero).ToDecimal());
    }

    [Fact]
    public void Change_given_is_already_out_of_the_net_figure()
    {
        // The caller passes tendered less change, not tendered. A €20 note against an €18.45
        // sale leaves €18.45 in the drawer, and counting the note would overstate the day by
        // the change handed back on every single sale.
        var expected = ShiftArithmetic.ExpectedCash(
            openingFloat: (Money)100m,
            netCashTendered: (Money)20m - (Money)1.55m,
            cashMovements: Money.Zero);

        Assert.Equal(118.45m, expected.ToDecimal());
    }
}

/// <summary>
/// The sign rule for a cash movement, which no check constraint can express.
/// </summary>
public sealed class CashRulesTests
{
    [Theory]
    [InlineData(CashMovementType.Drop, -50, true)]
    [InlineData(CashMovementType.Drop, 50, false)]
    [InlineData(CashMovementType.Payout, -20, true)]
    [InlineData(CashMovementType.Payout, 20, false)]
    [InlineData(CashMovementType.PettyCash, -5, true)]
    [InlineData(CashMovementType.PettyCash, 5, false)]
    public void Money_leaving_the_drawer_is_negative(CashMovementType type, decimal amount, bool valid)
    {
        // A drop entered as a positive leaves the drawer wrong by twice the amount, in the
        // direction nobody notices until close.
        Assert.Equal(valid, CashRules.IsSignConsistent(type, amount));
    }

    [Theory]
    [InlineData(-10, true)]
    [InlineData(10, true)]
    [InlineData(0, false)]
    public void A_correction_may_go_either_way_but_must_move_something(decimal amount, bool valid)
    {
        // The one type whose direction is unconstrained — correcting a float either way is the
        // point of it — but a correction that corrects nothing is a row with no meaning.
        Assert.Equal(valid, CashRules.IsSignConsistent(CashMovementType.Correction, amount));
    }

    [Fact]
    public void An_unknown_type_is_never_consistent()
    {
        Assert.False(CashRules.IsSignConsistent((CashMovementType)99, 10m));
    }

    [Fact]
    public void Every_type_is_one_a_client_may_send()
    {
        // Unlike stock movements, where Sale and Refund are written by the sale that caused
        // them and must be refused by hand. No cash movement type is system-written, so none
        // has to be withheld — stated as a test so the asymmetry is deliberate.
        Assert.Equal(Enum.GetValues<CashMovementType>(), CashRules.AllowedTypes);
    }
}
