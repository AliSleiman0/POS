using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// Splitting a cart discount across lines, tested on its own because the exactness argument
/// is the whole of it.
/// </summary>
public sealed class DiscountApportionmentTests
{
    [Fact]
    public void Shares_are_proportional_to_the_lines()
    {
        var shares = DiscountApportionment.Apportion(Nets(25m, 75m), (Money)10m);

        Assert.Equal(2.50m, shares[0].ToDecimal());
        Assert.Equal(7.50m, shares[1].ToDecimal());
    }

    [Fact]
    public void A_remainder_that_cannot_divide_evenly_lands_on_the_largest_line()
    {
        // 10.00 over 10 / 20: proportional shares are 3.3333… and 6.6666…, which cannot both
        // be stored and still sum to 10.00. The 0.0001 goes to the 20.00 line.
        var shares = DiscountApportionment.Apportion(Nets(10m, 20m), (Money)10m);

        Assert.Equal(3.3333m, shares[0].ToDecimal());
        Assert.Equal(6.6667m, shares[1].ToDecimal());
        Assert.Equal(10m, Money.Sum(shares).ToDecimal());
    }

    [Fact]
    public void The_remainder_goes_to_the_lowest_index_when_two_lines_tie()
    {
        // Determinism matters more than which line it is: a quote and the sale that follows
        // are the same cart, and a customer looking at both must see one number.
        var shares = DiscountApportionment.Apportion(Nets(10m, 10m, 10m), (Money)10m);

        Assert.Equal(10m, Money.Sum(shares).ToDecimal());
        Assert.Equal(3.3334m, shares[0].ToDecimal());
        Assert.Equal(3.3333m, shares[1].ToDecimal());
        Assert.Equal(3.3333m, shares[2].ToDecimal());
    }

    [Fact]
    public void A_discount_equal_to_the_basket_takes_every_line_to_zero()
    {
        var nets = Nets(3.33m, 6.67m);

        var shares = DiscountApportionment.Apportion(nets, (Money)10m);

        Assert.Equal(nets, shares);
        Assert.Equal(10m, Money.Sum(shares).ToDecimal());
    }

    [Fact]
    public void A_comped_basket_with_full_precision_lines_still_sums_exactly()
    {
        // The case the property test caught. 0.350 kg × 12.99 is 4.5465 exactly, but a
        // basket of such lines has a total that is only storable by luck — and the shares
        // are stored at four places whatever the nets carry. Returning the raw nets here
        // let the caller round each one independently, and independently-rounded parts do
        // not sum to the whole.
        var nets = new[] { (Money)4.54655m, (Money)4.54655m };
        var total = Money.Sum(nets);

        var shares = DiscountApportionment.Apportion(nets, total);

        Assert.All(shares, share => Assert.True(share.IsStorable));
        Assert.Equal(total, Money.Sum(shares));
    }

    [Fact]
    public void No_share_ever_exceeds_its_own_line()
    {
        // A share bigger than its line makes the line negative, which reads as a refund
        // buried inside a sale and reconciles against nothing.
        var nets = Nets(0.01m, 99.99m);

        var shares = DiscountApportionment.Apportion(nets, (Money)50m);

        for (var index = 0; index < shares.Count; index++)
        {
            Assert.True(shares[index] <= nets[index]);
        }
    }

    [Fact]
    public void A_zero_discount_gives_every_line_nothing()
    {
        var shares = DiscountApportionment.Apportion(Nets(10m, 20m), Money.Zero);

        Assert.All(shares, share => Assert.True(share.IsZero));
        Assert.Equal(2, shares.Count);
    }

    [Fact]
    public void A_discount_larger_than_the_basket_is_refused()
    {
        Assert.Throws<InvalidDiscountException>(
            () => DiscountApportionment.Apportion(Nets(10m), (Money)10.01m));
    }

    [Fact]
    public void A_discount_against_a_worthless_basket_is_refused()
    {
        // Rather than a DivideByZeroException, which is true and tells the caller nothing.
        Assert.Throws<InvalidDiscountException>(
            () => DiscountApportionment.Apportion(Nets(0m, 0m), (Money)1m));

        Assert.Throws<InvalidDiscountException>(
            () => DiscountApportionment.Apportion([], (Money)1m));
    }

    [Fact]
    public void A_negative_discount_is_refused_rather_than_read_as_a_surcharge()
    {
        Assert.Throws<InvalidDiscountException>(
            () => DiscountApportionment.Apportion(Nets(10m), (Money)(-1m)));
    }

    private static Money[] Nets(params decimal[] amounts) => [.. amounts.Select(a => (Money)a)];
}
