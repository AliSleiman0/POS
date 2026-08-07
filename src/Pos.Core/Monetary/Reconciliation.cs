namespace Pos.Core.Monetary;

/// <summary>
/// Making the parts of a total add up to it.
/// </summary>
/// <remarks>
/// <b>Independently rounded parts do not sum to the whole.</b> It is the same arithmetic fact
/// behind three different bugs in this codebase, which is why it lives in one place:
/// <list type="bullet">
/// <item>a cart discount apportioned across lines that comes to €4.99 of a €5.00 promise —
/// <see cref="Pricing.DiscountApportionment"/>, which solves it inline because it also has to
/// decide the proportional split;</item>
/// <item>a receipt whose VAT lines do not add up to its VAT total, which is the figure a tax
/// authority reads;</item>
/// <item>a Z-report whose tax-by-rate rows do not add up to its headline tax, because the
/// header was rounded once per sale and the lines are stored at four decimal places.</item>
/// </list>
/// The last two are this class. Both were found the same way — by a test that summed the parts
/// and compared, rather than by looking at a printout, because a cent is invisible on a
/// printout and obvious in an assertion.
/// </remarks>
public static class Reconciliation
{
    /// <summary>
    /// Rounds each part to the payable scale and puts the leftover on the largest of them, so
    /// that the parts sum to <paramref name="target"/> identically.
    /// </summary>
    /// <param name="parts">The unrounded parts, in a deterministic order the caller chose.</param>
    /// <param name="target">The whole they must add up to — an amount already rounded once.</param>
    /// <remarks>
    /// Largest <b>by magnitude</b>, not by value: a refund's amounts are all negative, and
    /// picking the greatest of those would put the residue on the smallest group. Ties go to the
    /// lowest index, so two identical sales produce identical output — which matters because a
    /// customer may be holding a receipt for one and a reprint of the other.
    /// <para>
    /// The residue is at most half a unit in the last place per part, so on any real basket it
    /// is a fraction of a cent. It is <i>defined</i> as what is left over rather than computed,
    /// which is what makes the sum exact by construction instead of exact in the cases somebody
    /// tried.
    /// </para>
    /// </remarks>
    public static Money[] RoundToSum(IReadOnlyList<Money> parts, Money target)
    {
        ArgumentNullException.ThrowIfNull(parts);

        var rounded = new Money[parts.Count];

        for (var index = 0; index < parts.Count; index++)
        {
            rounded[index] = parts[index].Round();
        }

        if (parts.Count == 0)
        {
            // Nothing to carry a residue. Reached for a scope with no lines in it — a day with
            // no sales — where the target is zero anyway, and returning empty keeps that a
            // report of zeroes rather than an exception.
            return rounded;
        }

        var residue = target - Money.Sum(rounded);

        if (residue.IsZero)
        {
            return rounded;
        }

        var largest = 0;

        for (var index = 1; index < parts.Count; index++)
        {
            if (parts[index].Abs() > parts[largest].Abs())
            {
                largest = index;
            }
        }

        rounded[largest] += residue;

        return rounded;
    }
}
