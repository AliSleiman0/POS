using Pos.Core.Monetary;

namespace Pos.Core.Shifts;

/// <summary>
/// The arithmetic a Z-report does beyond adding columns up.
/// </summary>
/// <remarks>
/// Beside <see cref="ShiftArithmetic"/>, which stays the authority on expected cash and
/// variance. Pure, so the sums a shop reconciles against its drawer can be read and tested
/// without a database — the queries that produce the inputs live in <c>Pos.Data</c>, where they
/// have to be raw SQL because EF cannot aggregate a value-converted <c>Money</c>.
/// </remarks>
public static class ZReportArithmetic
{
    /// <summary>
    /// What the average customer spent.
    /// </summary>
    /// <remarks>
    /// Zero for a day with no transactions, not a divide-by-zero and not <c>NaN</c>. §6.3's exit
    /// criterion is that a day with no sales renders as zeroes rather than an error, and a shop
    /// that opened and sold nothing still has to cash up against its float.
    /// <para>
    /// Rounded to the payable scale for display. It is a statistic and not an amount anyone
    /// hands over, so rounding it changes nothing that has to reconcile — but an unrounded
    /// <c>4.116666…</c> on a printout reads as a bug.
    /// </para>
    /// </remarks>
    public static Money AverageBasket(Money total, int transactionCount) =>
        transactionCount <= 0 ? Money.Zero : (total / transactionCount).Round();
}
