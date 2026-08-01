using Pos.Core.Exceptions;
using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// Turns a cart into the amounts a sale records. Pure: no database, no clock, no I/O.
/// </summary>
/// <remarks>
/// The pipeline, in this order and no other:
/// <code>
/// line gross (qty × unit price, full precision)
///   → line discount
///   → cart discount, apportioned across lines
///   → tax, on the discounted amount
///   → header totals, rounded once
///   → cash rounding
/// </code>
/// <para>
/// <b>Tax comes after both discounts</b>, never before. Taxing the pre-discount amount charges
/// a customer tax on money they did not spend, and the error scales with the discount.
/// </para>
/// <para>
/// <b>This is the only place a cart becomes amounts.</b> <c>POST /sales</c> and
/// <c>POST /sales/quote</c> both call it with a <see cref="Cart"/> built by one shared
/// function, which is the mechanical reason a quote and the sale that follows it cannot
/// disagree. Two implementations of tax and discount rules will differ eventually, and the
/// place it surfaces is a customer disputing a receipt at a counter.
/// </para>
/// </remarks>
public static class PricingEngine
{
    /// <summary>Prices <paramref name="cart"/>.</summary>
    /// <exception cref="InvalidDiscountException">A discount exceeds what it applies to.</exception>
    public static PricedSale Price(Cart cart)
    {
        ArgumentNullException.ThrowIfNull(cart);

        // 1. Gross and the line's own discount, at full precision. A zero-quantity line, a
        //    zero-price line and a fully-discounted line are all priced without complaint —
        //    a free gift is a real line. The endpoint refuses an empty cart and a zero
        //    quantity as UI slips, which is a different question from whether they compute.
        var gross = new Money[cart.Lines.Count];
        var nets = new Money[cart.Lines.Count];

        for (var index = 0; index < cart.Lines.Count; index++)
        {
            var line = cart.Lines[index];

            if (line.LineDiscount.IsNegative)
            {
                throw new InvalidDiscountException(
                    $"Line {index + 1} carries a negative discount.");
            }

            gross[index] = line.UnitPrice * line.Quantity;

            if (line.LineDiscount > gross[index])
            {
                throw new InvalidDiscountException(
                    $"Line {index + 1}'s discount is larger than the line.");
            }

            nets[index] = gross[index] - line.LineDiscount;
        }

        // 2. The cart discount, spread over the lines. See DiscountApportionment for why it
        //    is spread rather than subtracted from the total.
        var shares = DiscountApportionment.Apportion(nets, cart.CartDiscount);

        // 3. Tax per line, on what is left after both discounts.
        var priced = new PricedLine[cart.Lines.Count];

        var payable = Money.Zero;
        var taxTotal = Money.Zero;
        var discountTotal = Money.Zero;

        for (var index = 0; index < cart.Lines.Count; index++)
        {
            var line = cart.Lines[index];
            var taxable = nets[index] - shares[index];

            var tax = TaxCalculator.TaxOn(taxable, line.TaxRate, cart.TaxMode);

            // What this line contributes to the amount payable. In Exclusive mode tax is
            // added; in Inclusive mode the discounted price already contains it.
            var lineTotal = cart.TaxMode == Entities.TaxMode.Exclusive ? taxable + tax : taxable;

            // Subtotal and discount are both recorded net of tax, in both modes, so that the
            // sale-level identity holds however the tenant prices. In Inclusive mode that
            // means dividing the tax back out of each.
            var subtotal = TaxCalculator.NetOfTax(gross[index], line.TaxRate, cart.TaxMode);
            var discount = TaxCalculator.NetOfTax(
                line.LineDiscount + shares[index], line.TaxRate, cart.TaxMode);

            priced[index] = new PricedLine(
                Source: line,
                LineNumber: index + 1,
                Gross: gross[index].RoundToStorage(),
                Subtotal: subtotal.RoundToStorage(),
                Discount: discount.RoundToStorage(),
                CartDiscountShare: shares[index].RoundToStorage(),
                Tax: tax.RoundToStorage(),
                Total: lineTotal.RoundToStorage());

            // Accumulated at FULL precision, from the unrounded values — not from the
            // rounded ones just stored. Summing the rounded lines is the penny-off bug.
            payable += lineTotal;
            taxTotal += tax;
            discountTotal += discount;
        }

        // 4. Rounded once, here, and nowhere else.
        var payableRounded = payable.Round();
        var taxRounded = taxTotal.Round();
        var discountRounded = discountTotal.Round();

        // Subtotal is DERIVED, not independently summed, and this is the deliberate call in
        // the whole pipeline. DATA-MODEL.md invariant 1 says
        //     Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment
        // must hold on the STORED two-decimal values, in both tax modes. Four independently
        // rounded sums cannot guarantee that: each can be up to half a cent out, and the
        // identity then fails by a cent on some baskets and not others.
        //
        // One of the four has to absorb the residue, and Subtotal is the right one because it
        // is the only one never reconciled against anything outside the system — TaxTotal
        // against a VAT return, DiscountTotal against a promotions report, Total against the
        // cash in the drawer. It also stays within a cent of the natural sum, so nothing is
        // being distorted to make the arithmetic work.
        var subtotalRounded = payableRounded + discountRounded - taxRounded;

        // 5. Cash rounding, last, on the payable total only. Recorded, never absorbed.
        var adjustment = CashRounding.AdjustmentFor(payableRounded, cart.CashRoundingIncrement);

        return new PricedSale(
            Lines: priced,
            Subtotal: subtotalRounded,
            DiscountTotal: discountRounded,
            TaxTotal: taxRounded,
            RoundingAdjustment: adjustment,
            Total: payableRounded + adjustment);
    }
}
