using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// The one cart in this suite that was computed on paper first.
/// </summary>
/// <remarks>
/// The phase doc asks for this explicitly, and the reason is worth restating: every other test
/// here can only confirm the engine is <i>consistent</i>. Consistency is exactly what a
/// pricing bug preserves — a wrong rule applied uniformly produces totals that agree with
/// themselves, satisfy invariant 1, and are wrong on every receipt.
/// <para>
/// This cart is the awkward one on purpose: inclusive tax (so the rate has to be divided out
/// rather than added), two different rates (so a single cart-level rate would be wrong for one
/// of them), and a cart discount that does not divide evenly (so the apportionment remainder
/// is exercised).
/// </para>
/// <para>
/// <b>Worked by hand:</b>
/// <code>
/// Line 1: 3 × 4.9900 @ 23% inclusive  → gross 14.9700
/// Line 2: 2 × 1.5000 @  0% inclusive  → gross  3.0000
/// Cart total 17.9700, cart discount 5.0000
///
/// share₁ = 5 × 14.97/17.97 = 4.16527…  → 4.1653 (+0.0000 remainder)
/// share₂ = 5 ×  3.00/17.97 = 0.83472…  → 0.8347
/// 4.1653 + 0.8347 = 5.0000 exactly ✓
///
/// taxable₁ = 14.9700 − 4.1653 = 10.8047 ; tax₁ = 10.8047 × 0.23/1.23 = 2.02047…
/// taxable₂ =  3.0000 − 0.8347 =  2.1653 ; tax₂ = 0 (zero-rated)
///
/// Total         = round₂(10.8047 + 2.1653) = 12.97
/// TaxTotal      = round₂(2.02047…)         =  2.02
/// DiscountTotal = round₂(4.1653/1.23 + 0.8347/1) = round₂(3.38642… + 0.8347) = 4.22
/// Subtotal      = 12.97 + 4.22 − 2.02      = 15.17   (derived)
///
/// Invariant 1: 15.17 − 4.22 + 2.02 + 0.00 = 12.97 ✓
/// </code>
/// </para>
/// </remarks>
public sealed class HandCheckedCartTests
{
    [Fact]
    public void An_inclusive_mixed_rate_cart_with_a_cart_discount_matches_the_paper_working()
    {
        var sale = PricingEngine.Price(new Cart(
            [
                Line(quantity: 3m, unitPrice: 4.9900m, taxRate: 0.2300m),
                Line(quantity: 2m, unitPrice: 1.5000m, taxRate: 0.0000m),
            ],
            CartDiscount: (Money)5.0000m,
            TaxMode: TaxMode.Inclusive));

        Assert.Equal(12.97m, sale.Total.ToDecimal());
        Assert.Equal(2.02m, sale.TaxTotal.ToDecimal());
        Assert.Equal(4.22m, sale.DiscountTotal.ToDecimal());
        Assert.Equal(15.17m, sale.Subtotal.ToDecimal());
        Assert.Equal(0m, sale.RoundingAdjustment.ToDecimal());

        // The apportionment, to the last hundredth of a cent, and the remainder that makes the
        // two shares sum to exactly the five euros promised.
        Assert.Equal(4.1653m, sale.Lines[0].CartDiscountShare.ToDecimal());
        Assert.Equal(0.8347m, sale.Lines[1].CartDiscountShare.ToDecimal());
        Assert.Equal(
            5.0000m,
            Money.Sum(sale.Lines.Select(l => l.CartDiscountShare)).ToDecimal());

        // The zero-rated line carries no tax, which a single cart-level rate would have got
        // wrong — and the discount is what makes that visible rather than incidental.
        Assert.Equal(0m, sale.Lines[1].Tax.ToDecimal());

        PricingEngineTests.AssertInvariantOne(sale);
    }

    [Fact]
    public void The_same_cart_priced_exclusively_is_a_different_sale()
    {
        // The control that gives the test above its meaning: if the mode were ignored, both
        // would agree. Exclusive treats 14.97 as net and adds tax on top, so the customer pays
        // more than the shelf said.
        var exclusive = PricingEngine.Price(new Cart(
            [
                Line(quantity: 3m, unitPrice: 4.9900m, taxRate: 0.2300m),
                Line(quantity: 2m, unitPrice: 1.5000m, taxRate: 0.0000m),
            ],
            CartDiscount: (Money)5.0000m,
            TaxMode: TaxMode.Exclusive));

        // 10.8047 × 1.23 + 2.1653 = 13.28978… + 2.1653 = 15.45508… → 15.46
        Assert.Equal(15.46m, exclusive.Total.ToDecimal());
        Assert.Equal(2.49m, exclusive.TaxTotal.ToDecimal());

        PricingEngineTests.AssertInvariantOne(exclusive);
    }

    private static CartLine Line(decimal quantity, decimal unitPrice, decimal taxRate) =>
        new(
            Guid.CreateVersion7(),
            "Hand-checked line",
            quantity,
            (Money)unitPrice,
            taxRate,
            Money.Zero);
}
