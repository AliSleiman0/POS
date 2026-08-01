using Pos.Core.Catalog;
using Pos.Core.Entities;

namespace Pos.Core.Inventory;

/// <summary>
/// What a stock movement has to look like before anything writes it. Pure, so the rules can
/// be read and tested without a database.
/// </summary>
/// <remarks>
/// These are the rules that keep a ledger legible after a year of use: a direction that
/// agrees with the reason given, a quantity the column holds exactly, and a set of types a
/// human may write by hand. None of them can be expressed as a check constraint over a single
/// column, which is why they live here rather than in the migration.
/// </remarks>
public static class StockRules
{
    /// <summary>
    /// Whether a movement quantity survives <c>numeric(19,4)</c> exactly, in either direction.
    /// </summary>
    /// <remarks>
    /// Signed, unlike <see cref="CatalogRules.IsStorableAmount"/>: waste and sales are
    /// negative, and a rule that refused them would only be discovered by the first person to
    /// write off a broken bottle.
    /// </remarks>
    public static bool IsStorableQuantity(decimal value) =>
        CatalogRules.IsStorableSignedAmount(value);

    /// <summary>
    /// Whether the sign of <paramref name="quantity"/> agrees with what
    /// <paramref name="type"/> means.
    /// </summary>
    /// <remarks>
    /// A receipt of −5 and a waste of +5 are both a typed minus sign away from a stock figure
    /// that is wrong by twice the quantity, in the direction nobody notices until stocktake.
    /// Zero is refused for every type: a movement that moves nothing is a row that will be
    /// read as evidence of something happening.
    /// </remarks>
    public static bool IsSignConsistent(StockMovementType type, decimal quantity) => type switch
    {
        StockMovementType.Receive => quantity > 0m,
        StockMovementType.Refund => quantity > 0m,
        StockMovementType.Sale => quantity < 0m,
        StockMovementType.Waste => quantity < 0m,

        // Either direction, because both are corrections. A recount that agreed with the
        // ledger implies no delta and so no row.
        StockMovementType.Adjust => quantity != 0m,
        StockMovementType.Recount => quantity != 0m,

        _ => false,
    };

    /// <summary>
    /// Whether a person may write this type directly through <c>POST /stock/adjustments</c>.
    /// </summary>
    /// <remarks>
    /// <c>Sale</c> and <c>Refund</c> are written by the sale that caused them (Phase 3) and
    /// carry a <c>SaleId</c>; accepting them by hand would let someone fabricate sales
    /// movements with no sale behind them, and the ledger would stop reconciling with the
    /// takings. <c>Recount</c> is refused for a duller reason — a recount states an absolute
    /// count and the movement is the delta, which is a count-sheet feature that does not
    /// exist yet, and accepting a raw delta labelled <c>Recount</c> would misrecord it.
    /// </remarks>
    public static bool IsManualAdjustment(StockMovementType type) => type is
        StockMovementType.Receive or
        StockMovementType.Adjust or
        StockMovementType.Waste;

    /// <summary>The types a person may write, for an error message that lists them.</summary>
    public static IReadOnlyList<StockMovementType> ManualAdjustmentTypes { get; } =
        [.. Enum.GetValues<StockMovementType>().Where(IsManualAdjustment)];
}
