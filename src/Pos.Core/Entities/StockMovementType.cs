namespace Pos.Core.Entities;

/// <summary>
/// Why a quantity moved. The <c>Quantity</c> beside it carries the direction.
/// </summary>
/// <remarks>
/// Stored as text through <c>HasEnumAsText</c>, so these names are the values in
/// <c>ck_stock_movement_type_allowed</c> and in every row already written. Renaming a member
/// rewrites the constraint in the next migration and orphans the history — the ledger is
/// append-only precisely so that history stays readable, and a rename undoes that.
/// <para>
/// Both a reason and a direction, deliberately. "Minus three" answers nothing six months
/// later; "three wasted, damaged in transit" is the record shrinkage investigations need.
/// </para>
/// </remarks>
public enum StockMovementType
{
    /// <summary>Goods arriving from a supplier. Positive.</summary>
    Receive,

    /// <summary>A correction in either direction, with a reason. Never zero.</summary>
    Adjust,

    /// <summary>Sold. Negative, and written by the sale rather than by hand (Phase 3).</summary>
    Sale,

    /// <summary>Returned. Positive, and written by the refund (Phase 3).</summary>
    Refund,

    /// <summary>Damaged, expired or otherwise written off. Negative.</summary>
    Waste,

    /// <summary>
    /// The delta a physical count implies. Never zero — a count that agrees with the ledger
    /// moves nothing and is not worth a row.
    /// </summary>
    Recount,
}
