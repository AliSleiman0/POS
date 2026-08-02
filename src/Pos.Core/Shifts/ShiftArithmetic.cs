using Pos.Core.Monetary;

namespace Pos.Core.Shifts;

/// <summary>
/// What should be in the drawer, and how far off it was.
/// </summary>
/// <remarks>
/// The single report an owner checks daily. Pure, so the arithmetic can be read and tested
/// without a database — the queries that produce its inputs live in <c>Pos.Data</c>, where
/// they have to be raw SQL because EF cannot aggregate a value-converted <c>Money</c>.
/// </remarks>
public static class ShiftArithmetic
{
    /// <summary>
    /// <c>float + net cash taken + cash movements</c>.
    /// </summary>
    /// <param name="openingFloat">What was in the drawer at open.</param>
    /// <param name="netCashTendered">
    /// Cash tendered less change given, over every <b>non-voided</b> sale and refund on this
    /// shift. Refund tenders are negative, so returns subtract with no special case.
    /// </param>
    /// <param name="cashMovements">
    /// The sum of this shift's cash movements. Signed, and mostly negative — drops and payouts
    /// take money out.
    /// </param>
    /// <remarks>
    /// <b>Voided sales are excluded, not subtracted.</b> A void hands the cash straight back,
    /// so the money never stayed in the drawer; treating it as a sale followed by a negative
    /// would give the same total while making the Z-report claim takings that did not happen.
    /// </remarks>
    public static Money ExpectedCash(Money openingFloat, Money netCashTendered, Money cashMovements) =>
        openingFloat + netCashTendered + cashMovements;

    /// <summary>
    /// <c>counted − expected</c>. Negative means the drawer is <b>short</b>.
    /// </summary>
    /// <remarks>
    /// Stored on the shift rather than recomputed on read. Recomputing it later would silently
    /// change a historical variance whenever anything about the underlying sales changed —
    /// the same class of mistake as joining a report to the current product price.
    /// </remarks>
    public static Money Variance(Money countedCash, Money expectedCash) => countedCash - expectedCash;
}
