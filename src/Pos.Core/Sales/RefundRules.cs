using Pos.Core.Monetary;

namespace Pos.Core.Sales;

/// <summary>
/// How much of a sale line is still refundable, and what a partial refund is worth.
/// </summary>
/// <remarks>
/// Pure, and separate from the pricing engine because it answers a different question. The
/// engine prices a cart the shop is selling; this re-prices something it already sold, from
/// that sale's own snapshots.
/// </remarks>
public static class RefundRules
{
    /// <summary>
    /// What is left to refund on a line.
    /// </summary>
    /// <param name="originalQuantity">The quantity sold. Positive.</param>
    /// <param name="alreadyRefunded">
    /// The quantity already returned against this line, as a positive magnitude, summed across
    /// every refund that has not itself been voided.
    /// </param>
    /// <remarks>
    /// "Not itself been voided" is the part that is easy to miss: voiding a refund puts the
    /// goods back on the customer's side of the counter, so the quantity becomes refundable
    /// again. Counting a voided refund would let a customer be refused a refund they never
    /// received.
    /// </remarks>
    public static decimal RemainingQuantity(decimal originalQuantity, decimal alreadyRefunded) =>
        originalQuantity - alreadyRefunded;

    /// <summary>Whether <paramref name="requested"/> can still be refunded against a line.</summary>
    public static bool CanRefund(decimal originalQuantity, decimal alreadyRefunded, decimal requested) =>
        requested > 0m && requested <= RemainingQuantity(originalQuantity, alreadyRefunded);

    /// <summary>
    /// The share of a line's discount that belongs to <paramref name="quantity"/> units of it.
    /// </summary>
    /// <remarks>
    /// A customer returning one of three discounted items gets a third of the discount back,
    /// not the whole of it and not none of it. Refunding the undiscounted price would hand
    /// back more than was paid — which is a real way for a shop to lose money on a returns
    /// policy, and it looks correct on the receipt.
    /// <para>
    /// Computed at full precision and rounded by the caller, once, with everything else. A
    /// per-unit figure rounded here and then multiplied is the per-line rounding bug wearing a
    /// different hat.
    /// </para>
    /// </remarks>
    public static Money DiscountShare(Money lineDiscount, decimal quantity, decimal originalQuantity)
    {
        if (originalQuantity <= 0m)
        {
            // A zero-quantity line cannot be refunded, and dividing by it would say so with a
            // DivideByZeroException rather than with something a caller can act on.
            return Money.Zero;
        }

        return lineDiscount * (quantity / originalQuantity);
    }
}
