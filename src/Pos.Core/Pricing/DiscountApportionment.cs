using Pos.Core.Exceptions;
using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// Spreads a cart-level discount across the lines it applies to.
/// </summary>
/// <remarks>
/// <b>Why apportion at all, rather than subtract the discount from the total?</b> Because tax
/// is per line. A basket holding a zero-rated loaf and a standard-rated bottle, given €5 off,
/// has a different tax total depending on which of the two the €5 came off. Subtracting at the
/// end would have to pick a rate to reduce the tax by, and any single choice is wrong for some
/// basket. Apportioning first means each line is taxed at its own rate on its own discounted
/// amount, and the question never arises.
/// <para>
/// <b>The parts must sum to exactly the whole.</b> A discount that apportions to €4.99 of a
/// €5.00 promise is a cent the shop gave away and cannot explain; one that apportions to €5.01
/// is a cent it took. The remainder step below is what makes the sum exact by construction
/// rather than exact in the cases somebody tried.
/// </para>
/// </remarks>
public static class DiscountApportionment
{
    /// <summary>
    /// Splits <paramref name="cartDiscount"/> across <paramref name="lineNets"/> in proportion
    /// to each line's net amount.
    /// </summary>
    /// <param name="lineNets">Each line's amount after its own line discount.</param>
    /// <param name="cartDiscount">The amount to spread. Zero is ordinary.</param>
    /// <returns>One share per line, in the same order, summing to <paramref name="cartDiscount"/>.</returns>
    /// <exception cref="InvalidDiscountException">
    /// The discount exceeds the basket, or there is nothing to discount.
    /// </exception>
    public static IReadOnlyList<Money> Apportion(IReadOnlyList<Money> lineNets, Money cartDiscount)
    {
        ArgumentNullException.ThrowIfNull(lineNets);

        if (cartDiscount.IsNegative)
        {
            throw new InvalidDiscountException("A cart discount cannot be negative.");
        }

        if (cartDiscount.IsZero)
        {
            return [.. lineNets.Select(_ => Money.Zero)];
        }

        var total = Money.Sum(lineNets);

        if (cartDiscount > total)
        {
            throw new InvalidDiscountException(
                "A cart discount cannot be larger than the amount it applies to.");
        }

        // Reached when the basket is worth nothing and a discount was still asked for —
        // every line free already, or an empty cart. Proportional shares of zero are
        // undefined, and dividing by the total below would say so with a DivideByZeroException
        // rather than with something a caller can act on.
        if (total.IsZero)
        {
            throw new InvalidDiscountException(
                "There is nothing for a cart discount to apply to.");
        }

        // The whole basket is being comped: each line's share is simply the whole line. Not
        // returned directly, though — it still goes through the rounding pass below, because
        // a line net carries full precision (0.350 kg × 12.99 is 4.5465) and a share is
        // stored at numeric(19,4). Returning the raw nets here would let the caller round
        // each one independently, and independently-rounded parts do not sum to the whole.
        // That is the same penny-off bug one level down, and it is what the property test
        // Apportioned_discounts_always_sum_to_the_discount_given caught.
        var comped = cartDiscount == total;

        var shares = new Money[lineNets.Count];

        for (var index = 0; index < lineNets.Count; index++)
        {
            var raw = comped
                ? lineNets[index]
                : cartDiscount * Money.Ratio(lineNets[index], total);

            // Clamped at the line's own net so a share can never make a line negative, and
            // clamped against the *stored* net because that is what the share will sit
            // beside on the sale line — comparing a rounded share to an unrounded net would
            // let the clamp itself reintroduce full precision.
            var ceiling = lineNets[index].RoundToStorage();
            var rounded = raw.RoundToStorage();

            shares[index] = rounded > ceiling ? ceiling : rounded;
        }

        // The remainder is *defined* as what is left over, so the shares plus the remainder
        // are identically the discount given — in either direction, because rounding can have
        // lost or gained. It is at most half a unit in the last place per line, so on any
        // real basket it is a fraction of a cent.
        var remainder = cartDiscount - Money.Sum(shares);

        if (!remainder.IsZero)
        {
            // Onto the largest line, ties to the lowest index. Largest because it is the line
            // best able to absorb it without the clamp above biting; deterministic because two
            // identical carts must price identically — a quote and the sale that follows it
            // are the same cart, and a customer looking at both should see one number.
            var target = 0;

            for (var index = 1; index < lineNets.Count; index++)
            {
                if (lineNets[index] > lineNets[target])
                {
                    target = index;
                }
            }

            shares[target] += remainder;
        }

        return shares;
    }
}
