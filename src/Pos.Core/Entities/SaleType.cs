namespace Pos.Core.Entities;

/// <summary>Which direction money moved.</summary>
/// <remarks>
/// A refund is a <b>separate sale row</b> of type <see cref="Refund"/> with negative amounts
/// and <c>OriginalSaleId</c> set, never an edit of the sale it reverses. That is CLAUDE.md
/// invariant 4: a completed financial record is append-only, and a correction is a new linked
/// row. Editing the original would leave no evidence that anything was returned.
/// </remarks>
public enum SaleType
{
    /// <summary>Goods out, money in. Positive amounts.</summary>
    Sale = 0,

    /// <summary>Goods back, money out. Negative amounts, linked to the original.</summary>
    Refund = 1,
}
