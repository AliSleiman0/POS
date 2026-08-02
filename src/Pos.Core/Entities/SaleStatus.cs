namespace Pos.Core.Entities;

/// <summary>Whether a sale still counts.</summary>
/// <remarks>
/// There is no <c>Draft</c> and no <c>Pending</c>. A row in this table is a completed
/// transaction; a cart that has not been paid for lives on the register, not in the database.
/// <para>
/// <see cref="Voided"/> is the <i>only</i> mutation a completed sale ever receives, and it
/// touches nothing but the void columns — the amounts and the sale number stay exactly as
/// they were. A void also writes compensating stock movements rather than deleting the
/// original ones, so the ledger still explains where the goods went and came back.
/// </para>
/// </remarks>
public enum SaleStatus
{
    /// <summary>Paid for and final.</summary>
    Completed = 0,

    /// <summary>Reversed in full, with compensating stock movements. Never deleted.</summary>
    Voided = 1,
}
