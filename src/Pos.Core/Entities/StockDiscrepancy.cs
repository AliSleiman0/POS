using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A sale that took a product's on-hand below zero. An append-only observation.
/// </summary>
/// <remarks>
/// <b>Insufficient stock does not block a sale.</b> The customer is standing at the counter
/// holding the item; refusing to sell it is the wrong behaviour and is how POS systems get
/// thrown out. The sale completes, stock goes negative, and this row is written for staff to
/// investigate — flag rather than silently corrupt, per DECISIONS.md.
/// <para>
/// Written when the on-hand is negative <i>after</i> the movement, not when stock "looked
/// insufficient" before it. Selling the last three of three is an ordinary sale that lands on
/// zero; going from 1 to −2 means the shop is holding less than nothing, and that is the
/// condition a stocktake has to explain.
/// </para>
/// <para>
/// <b>Never resolved, and it has no resolution columns.</b> A discrepancy is an observation
/// like a stock movement, and "resolving" one means writing a <c>Recount</c> — the count-sheet
/// feature <c>StockRules</c> already defers. Adding <c>ResolvedAt</c>/<c>ResolvedBy</c> now
/// would be columns nothing reads and nothing sets.
/// </para>
/// </remarks>
public sealed class StockDiscrepancy : TenantEntity
{
    public Guid ProductId { get; set; }

    /// <summary>The sale that caused it.</summary>
    public Guid SaleId { get; set; }

    /// <summary>The specific line, so the investigation starts at the right item.</summary>
    public Guid SaleLineId { get; set; }

    /// <summary>How much the line asked for.</summary>
    public decimal QuantityRequested { get; set; }

    /// <summary>Where the on-hand ended up. Negative, by definition of this row existing.</summary>
    public decimal OnHandAfter { get; set; }

    /// <summary>Server-set from <c>TimeProvider</c>, and the order the list pages in.</summary>
    public DateTimeOffset DetectedAt { get; set; }
}
