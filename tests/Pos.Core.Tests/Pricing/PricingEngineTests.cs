using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// Worked examples, checked by hand, in both tax modes.
/// </summary>
/// <remarks>
/// Every expected figure below was computed on paper before it was written down. A test that
/// asserts whatever the code returned proves the code is <i>consistent</i>, not that it is
/// <i>correct</i> — and consistency is exactly what a pricing bug preserves.
/// </remarks>
public sealed class PricingEngineTests
{
    [Fact]
    public void An_exclusive_cart_adds_tax_to_the_discounted_line_amount()
    {
        // 2 × 10.00 = 20.00 gross, 5.00 off = 15.00 net, 23% tax = 3.45, total 18.45.
        // Tax on the DISCOUNTED 15.00, not on the gross 20.00 — that would be 4.60, and the
        // customer would be paying 1.15 of tax on money they did not spend.
        var sale = PricingEngine.Price(new Cart(
            [Line(quantity: 2m, unitPrice: 10.00m, taxRate: 0.23m, lineDiscount: 5.00m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        // Subtotal is the gross net-of-tax, BEFORE discount — 20.00, not 15.00. The invariant
        // subtracts DiscountTotal from it, so a post-discount subtotal would deduct the same
        // 5.00 twice. Worth stating: 15.00 is the intuitive reading and it is wrong.
        Assert.Equal(20.00m, sale.Subtotal.ToDecimal());
        Assert.Equal(5.00m, sale.DiscountTotal.ToDecimal());
        Assert.Equal(3.45m, sale.TaxTotal.ToDecimal());
        Assert.Equal(18.45m, sale.Total.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void An_inclusive_cart_extracts_tax_from_the_shelf_price()
    {
        // A shelf price of 12.30 at 23% already contains 2.30 of tax: 12.30 × 0.23/1.23.
        // The customer pays 12.30 — the same number on the label, which is the entire point
        // of inclusive pricing — and the shop owes 2.30 of it.
        var sale = PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 12.30m, taxRate: 0.23m, lineDiscount: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Inclusive));

        Assert.Equal(12.30m, sale.Total.ToDecimal());
        Assert.Equal(2.30m, sale.TaxTotal.ToDecimal());
        Assert.Equal(10.00m, sale.Subtotal.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void The_same_price_means_different_things_in_the_two_modes()
    {
        // Stated on its own because it is the reason TaxMode is fixed at onboarding and
        // refused afterwards. One stored price, two totals, and flipping the setting after
        // trading silently rewrites what every historical sale meant.
        CartLine[] lines = [Line(quantity: 1m, unitPrice: 100.00m, taxRate: 0.23m, lineDiscount: 0m)];

        var exclusive = PricingEngine.Price(new Cart(lines, Money.Zero, TaxMode.Exclusive));
        var inclusive = PricingEngine.Price(new Cart(lines, Money.Zero, TaxMode.Inclusive));

        Assert.Equal(123.00m, exclusive.Total.ToDecimal());
        Assert.Equal(100.00m, inclusive.Total.ToDecimal());
    }

    [Fact]
    public void Mixed_tax_rates_in_one_cart_are_taxed_per_line()
    {
        // A zero-rated loaf and a standard-rated bottle. Tax is 23% of the bottle alone, and
        // a single cart-level rate would have got both lines wrong.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 2.00m, taxRate: 0.00m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 10.00m, taxRate: 0.23m, lineDiscount: 0m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(2.30m, sale.TaxTotal.ToDecimal());
        Assert.Equal(14.30m, sale.Total.ToDecimal());

        Assert.Equal(0.00m, sale.Lines[0].Tax.ToDecimal());
        Assert.Equal(2.30m, sale.Lines[1].Tax.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void A_cart_discount_is_apportioned_with_the_remainder_on_the_largest_line()
    {
        // 10.00 off a 30.00 basket split 10 / 20. Proportional shares are 3.3333… and
        // 6.6666…, which do not sum to 10.00 at any finite scale — the remainder is what
        // closes the gap, and it lands on the 20.00 line.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 10.00m, taxRate: 0m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 20.00m, taxRate: 0m, lineDiscount: 0m),
            ],
            CartDiscount: (Money)10.00m,
            TaxMode: TaxMode.Exclusive));

        var shares = sale.Lines.Select(line => line.CartDiscountShare).ToArray();

        Assert.Equal(10.00m, Money.Sum(shares).ToDecimal());
        Assert.Equal(3.3333m, shares[0].ToDecimal());
        Assert.Equal(6.6667m, shares[1].ToDecimal());

        Assert.Equal(20.00m, sale.Total.ToDecimal());
        Assert.Equal(10.00m, sale.DiscountTotal.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void A_cart_discount_equal_to_the_cart_leaves_every_line_at_zero()
    {
        // A fully comped basket. Exact by definition rather than by rounding luck: the
        // engine special-cases it, so no line can be left holding a stray hundredth.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 3m, unitPrice: 3.33m, taxRate: 0.23m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 0.01m, taxRate: 0.23m, lineDiscount: 0m),
            ],
            CartDiscount: (Money)10.00m,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(0.00m, sale.Total.ToDecimal());
        Assert.Equal(0.00m, sale.TaxTotal.ToDecimal());
        Assert.All(sale.Lines, line => Assert.Equal(0.00m, line.Total.ToDecimal()));

        AssertInvariantOne(sale);
    }

    [Fact]
    public void A_hundred_percent_line_discount_is_priced_and_taxed_as_zero()
    {
        // Buy one get one free, as a line discount. The free line is still a line: it prints
        // on the receipt, it decrements stock, and it is taxed on nothing.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 5.00m, taxRate: 0.23m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 5.00m, taxRate: 0.23m, lineDiscount: 5.00m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(0.00m, sale.Lines[1].Total.ToDecimal());
        Assert.Equal(0.00m, sale.Lines[1].Tax.ToDecimal());
        Assert.Equal(6.15m, sale.Total.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void A_zero_price_line_is_priced_without_dividing_by_zero()
    {
        // Inclusive mode divides by (1 + rate), never by the price — so a zero-price line is
        // arithmetic, not an edge case. A carrier bag given away is exactly this.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 0.00m, taxRate: 0.23m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 12.30m, taxRate: 0.23m, lineDiscount: 0m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Inclusive));

        Assert.Equal(12.30m, sale.Total.ToDecimal());
        Assert.Equal(0.00m, sale.Lines[0].Total.ToDecimal());
    }

    [Fact]
    public void A_zero_quantity_line_contributes_nothing_rather_than_throwing()
    {
        // The engine has no opinion; POST /sales refuses it as a UI slip. Kept separate so
        // that moving the check does not silently change what the arithmetic does.
        var sale = PricingEngine.Price(new Cart(
            [Line(quantity: 0m, unitPrice: 5.00m, taxRate: 0.23m, lineDiscount: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(0.00m, sale.Total.ToDecimal());
    }

    [Fact]
    public void An_empty_cart_prices_to_zero()
    {
        var sale = PricingEngine.Price(new Cart([], Money.Zero, TaxMode.Exclusive));

        Assert.Equal(0.00m, sale.Total.ToDecimal());
        Assert.Empty(sale.Lines);
    }

    [Fact]
    public void A_weighed_line_keeps_its_full_precision_until_the_total()
    {
        // 0.350 kg at 12.99/kg is 4.5465 — four decimals that are real, and a fifth the
        // column would not hold. The line stores 4.5465 and the customer pays 4.55.
        var sale = PricingEngine.Price(new Cart(
            [Line(quantity: 0.350m, unitPrice: 12.99m, taxRate: 0m, lineDiscount: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(4.5465m, sale.Lines[0].Total.ToDecimal());
        Assert.Equal(4.55m, sale.Total.ToDecimal());
    }

    [Fact]
    public void The_engine_rounds_the_total_once_rather_than_each_line()
    {
        // The penny-off bug, pinned against the engine rather than against Money.Sum.
        //
        // Three lines of 0.335. Carried at full precision the payable is 1.005, which rounds
        // to 1.01. Rounding each line first gives 0.34 three times and a total of 1.02 — one
        // cent, on a three-item basket, in the direction the customer notices.
        //
        // This test exists because a deliberate break that accumulated `lineTotal.Round()`
        // instead of `lineTotal` passed every other test in this file: the worked examples
        // have no sub-cent parts, and the invariant-one property still holds because Subtotal
        // is derived from the same wrong number. Nothing else covers it.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 0.335m, taxRate: 0m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 0.335m, taxRate: 0m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 0.335m, taxRate: 0m, lineDiscount: 0m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(1.01m, sale.Total.ToDecimal());
        Assert.NotEqual(1.02m, sale.Total.ToDecimal());
    }

    [Fact]
    public void The_tax_total_is_rounded_once_rather_than_each_line()
    {
        // The same break, one column over, and it needs its own cart: a rate of 1 makes each
        // line's tax 0.335, so TaxTotal is 1.005 → 1.01 while the payable total lands on a
        // clean 2.01 and would not have noticed. TaxTotal is the figure that goes to a tax
        // authority, so a cent of drift here is the one worth catching separately.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 0.335m, taxRate: 1m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 0.335m, taxRate: 1m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 0.335m, taxRate: 1m, lineDiscount: 0m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(1.01m, sale.TaxTotal.ToDecimal());
        Assert.Equal(2.01m, sale.Total.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void The_discount_total_is_rounded_once_rather_than_each_line()
    {
        // And the third column. Three lines each discounted 0.335 off 1.00: DiscountTotal is
        // 1.005 → 1.01, not 1.02.
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 1.00m, taxRate: 0m, lineDiscount: 0.335m),
                Line(quantity: 1m, unitPrice: 1.00m, taxRate: 0m, lineDiscount: 0.335m),
                Line(quantity: 1m, unitPrice: 1.00m, taxRate: 0m, lineDiscount: 0.335m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(1.01m, sale.DiscountTotal.ToDecimal());

        // 3 × 0.665 is 1.995, which rounds away from zero to 2.00 — not down to 1.99.
        Assert.Equal(2.00m, sale.Total.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void Cash_rounding_moves_the_total_and_records_the_difference()
    {
        // 4.97 payable, 5c coins: the customer pays 4.95 and the sale carries -0.02 so the
        // drawer reconciles. Without the recorded adjustment the till is 2c short with
        // nothing to explain it.
        var sale = PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 4.97m, taxRate: 0m, lineDiscount: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive,
            CashRoundingIncrement: 0.05m));

        Assert.Equal(4.95m, sale.Total.ToDecimal());
        Assert.Equal(-0.02m, sale.RoundingAdjustment.ToDecimal());

        AssertInvariantOne(sale);
    }

    [Fact]
    public void Without_a_rounding_rule_there_is_no_adjustment()
    {
        var sale = PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 4.97m, taxRate: 0m, lineDiscount: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal(4.97m, sale.Total.ToDecimal());
        Assert.Equal(0.00m, sale.RoundingAdjustment.ToDecimal());
    }

    [Fact]
    public void A_cart_discount_larger_than_the_cart_is_refused()
    {
        var exception = Assert.Throws<InvalidDiscountException>(() => PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 10.00m, taxRate: 0m, lineDiscount: 0m)],
            CartDiscount: (Money)12.00m,
            TaxMode: TaxMode.Exclusive)));

        // Refused, not clamped to zero: "I typed 12 instead of 1.20" would otherwise become a
        // free basket that balances perfectly and looks deliberate in every report.
        Assert.Equal("invalid-discount", exception.ErrorType);
    }

    [Fact]
    public void A_line_discount_larger_than_the_line_is_refused()
    {
        Assert.Throws<InvalidDiscountException>(() => PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 10.00m, taxRate: 0m, lineDiscount: 10.01m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive)));
    }

    [Fact]
    public void A_negative_discount_is_refused_rather_than_read_as_a_surcharge()
    {
        // Otherwise "-5.00 off" is a five-euro surcharge the customer never agreed to, and
        // it would reconcile perfectly in every report.
        Assert.Throws<InvalidDiscountException>(() => PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 10.00m, taxRate: 0m, lineDiscount: -5.00m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive)));

        Assert.Throws<InvalidDiscountException>(() => PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 10.00m, taxRate: 0m, lineDiscount: 0m)],
            CartDiscount: (Money)(-5.00m),
            TaxMode: TaxMode.Exclusive)));
    }

    [Fact]
    public void A_discount_on_a_basket_worth_nothing_is_refused()
    {
        // Proportional shares of zero are undefined. Answering with a DivideByZeroException
        // would be true and useless; this says what the caller did.
        Assert.Throws<InvalidDiscountException>(() => PricingEngine.Price(new Cart(
            [Line(quantity: 1m, unitPrice: 0.00m, taxRate: 0m, lineDiscount: 0m)],
            CartDiscount: (Money)1.00m,
            TaxMode: TaxMode.Exclusive)));
    }

    [Fact]
    public void Lines_are_numbered_from_one_in_cart_order()
    {
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 1m, unitPrice: 1.00m, taxRate: 0m, lineDiscount: 0m),
                Line(quantity: 1m, unitPrice: 2.00m, taxRate: 0m, lineDiscount: 0m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        Assert.Equal([1, 2], [.. sale.Lines.Select(line => line.LineNumber)]);
    }

    internal static CartLine Line(
        decimal quantity,
        decimal unitPrice,
        decimal taxRate,
        decimal lineDiscount) =>
        new(
            ProductId: Guid.CreateVersion7(),
            Description: "Test line",
            Quantity: quantity,
            UnitPrice: (Money)unitPrice,
            TaxRate: taxRate,
            LineDiscount: (Money)lineDiscount);

    /// <summary>
    /// DATA-MODEL.md invariant 1, on the stored two-decimal values.
    /// </summary>
    /// <remarks>
    /// Asserted on every worked example above rather than once in isolation, because the
    /// identity is the thing that has to survive both tax modes, mixed rates, apportionment
    /// and cash rounding — and it is only interesting where those interact.
    /// </remarks>
    internal static void AssertInvariantOne(PricedSale sale)
    {
        Assert.Equal(
            sale.Total,
            sale.Subtotal - sale.DiscountTotal + sale.TaxTotal + sale.RoundingAdjustment);
    }
}
