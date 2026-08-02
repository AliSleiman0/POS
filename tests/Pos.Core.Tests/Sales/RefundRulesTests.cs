using Pos.Core.Monetary;
using Pos.Core.Sales;

namespace Pos.Core.Tests.Sales;

/// <summary>
/// How much of a line is still refundable, and what a partial return is worth.
/// </summary>
public sealed class RefundRulesTests
{
    [Fact]
    public void What_remains_is_what_was_sold_less_what_came_back()
    {
        Assert.Equal(2m, RefundRules.RemainingQuantity(3m, 1m));
        Assert.Equal(0m, RefundRules.RemainingQuantity(3m, 3m));
    }

    [Theory]
    [InlineData(3, 0, 3, true)]     // all of it, in one go
    [InlineData(3, 1, 2, true)]     // the rest of it
    [InlineData(3, 1, 3, false)]    // more than remains
    [InlineData(3, 3, 1, false)]    // nothing remains
    [InlineData(3, 0, 0, false)]    // a refund of nothing is not a refund
    [InlineData(3, 0, -1, false)]   // negative would ADD to what was sold
    public void A_refund_is_allowed_only_up_to_what_remains(
        decimal sold,
        decimal already,
        decimal requested,
        bool allowed)
    {
        Assert.Equal(allowed, RefundRules.CanRefund(sold, already, requested));
    }

    [Fact]
    public void A_fractional_quantity_can_be_partially_refunded()
    {
        // 0.350 kg of cheese sold, 0.100 kg returned. Weighed goods are ordinary.
        Assert.True(RefundRules.CanRefund(0.350m, 0m, 0.100m));
        Assert.Equal(0.250m, RefundRules.RemainingQuantity(0.350m, 0.100m));
    }

    [Fact]
    public void A_discount_comes_back_in_proportion_to_the_quantity()
    {
        // One of three items with 0.60 off the line: 0.20 of the discount comes back with it.
        // Refunding the undiscounted price would hand back more than was paid, and it would
        // look entirely correct on the receipt.
        Assert.Equal((Money)0.20m, RefundRules.DiscountShare((Money)0.60m, quantity: 1m, originalQuantity: 3m));
    }

    [Fact]
    public void Returning_the_whole_line_returns_the_whole_discount()
    {
        Assert.Equal((Money)0.60m, RefundRules.DiscountShare((Money)0.60m, quantity: 3m, originalQuantity: 3m));
    }

    [Fact]
    public void An_undiscounted_line_returns_nothing_extra()
    {
        Assert.Equal(Money.Zero, RefundRules.DiscountShare(Money.Zero, quantity: 1m, originalQuantity: 3m));
    }

    [Fact]
    public void The_share_keeps_full_precision_for_the_caller_to_round_once()
    {
        // One of three items with 1.00 off is 0.3333… — and it stays that way. Rounding a
        // per-unit figure here and multiplying it back up is the per-line rounding bug wearing
        // a different hat.
        var share = RefundRules.DiscountShare((Money)1.00m, quantity: 1m, originalQuantity: 3m);

        Assert.False(share.IsStorable);
        Assert.True(share.ToDecimal() > 0.3333m && share.ToDecimal() < 0.3334m);
    }

    [Fact]
    public void A_zero_quantity_line_shares_nothing_rather_than_dividing_by_zero()
    {
        // Answering with a DivideByZeroException would be true and useless.
        Assert.Equal(Money.Zero, RefundRules.DiscountShare((Money)1m, quantity: 1m, originalQuantity: 0m));
    }
}
