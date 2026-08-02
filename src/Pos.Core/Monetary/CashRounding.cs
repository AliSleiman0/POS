namespace Pos.Core.Monetary;

/// <summary>
/// Rounding a payable total to the smallest coin that actually exists.
/// </summary>
/// <remarks>
/// Some jurisdictions have withdrawn their 1c and 2c coins, so a cash total of €4.97 is paid
/// as €4.95. This is <b>not</b> the same operation as <see cref="Money.Round(int)"/>: that one
/// takes a full-precision amount to the currency's minor unit, this one takes an already-exact
/// amount to a coarser increment because of what is in the till.
/// <para>
/// <b>The difference is recorded, never absorbed.</b> The pricing pipeline stores the result
/// as <c>Sale.RoundingAdjustment</c>, so <c>Subtotal − DiscountTotal + TaxTotal +
/// RoundingAdjustment == Total</c> still holds exactly. Nudging the total instead would leave
/// the drawer over or short by an amount nothing in the system explains, which is precisely
/// the discrepancy a Z-report exists to surface.
/// </para>
/// <para>
/// An increment of zero means the jurisdiction has no such rule, which is the default and the
/// overwhelmingly common case. It is an exact no-op rather than a divide by zero.
/// </para>
/// </remarks>
public static class CashRounding
{
    /// <summary>
    /// Rounds <paramref name="total"/> to the nearest multiple of <paramref name="increment"/>.
    /// </summary>
    /// <param name="total">The payable amount, already at the currency's minor unit.</param>
    /// <param name="increment">
    /// The smallest coin, e.g. <c>0.05</c>. Zero means no cash rounding applies.
    /// </param>
    public static Money ToIncrement(Money total, decimal increment)
    {
        if (increment <= 0m)
        {
            return total;
        }

        var steps = Rounding.To(total.ToDecimal() / increment, 0);

        // Back to the storage scale, because steps × increment can carry more places than
        // either operand — 0.05 × 3 is exact, but an increment like 0.025 would not be.
        return new Money(Rounding.To(steps * increment, Rounding.StorageScale));
    }

    /// <summary>
    /// What <see cref="ToIncrement"/> would add to <paramref name="total"/>: the value stored
    /// as <c>Sale.RoundingAdjustment</c>.
    /// </summary>
    /// <remarks>
    /// Signed, and either sign is ordinary — €4.97 rounds down by 2c, €4.98 rounds up by 2c.
    /// </remarks>
    public static Money AdjustmentFor(Money total, decimal increment) =>
        ToIncrement(total, increment) - total;
}
