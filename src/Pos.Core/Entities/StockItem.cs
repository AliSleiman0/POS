using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// How much of a product is on hand. One row per stock-tracking product.
/// </summary>
/// <remarks>
/// <b><see cref="OnHand"/> is a cached projection, not the truth.</b> The truth is the
/// <c>StockMovement</c> ledger (Phase 2.4); this row exists so a register can price and
/// warn without summing a ledger on every scan, and it can always be rebuilt from one.
/// The question staff actually ask is never "what is on hand" but "why is on hand wrong?",
/// and only a ledger answers that.
/// <para>
/// <b>There is deliberately no <c>OnHand &gt;= 0</c> constraint.</b> Overselling is real:
/// stock counts drift, and a till that refuses to sell an item the customer is holding is
/// worse than a negative number. Phase 3.6 flags the oversell for review rather than
/// refusing it or silently correcting it — a constraint here would turn that into a failed
/// checkout.
/// </para>
/// </remarks>
public sealed class StockItem : TenantEntity
{
    public Guid ProductId { get; set; }

    /// <summary>
    /// Current quantity, <c>numeric(19,4)</c> — fractional for weighed and measured goods.
    /// May be negative; see the remarks on the class.
    /// </summary>
    public decimal OnHand { get; set; }

    /// <summary>The level at or below which this product should be reordered. Null = never flagged.</summary>
    public decimal? ReorderPoint { get; set; }

    /// <summary>
    /// Postgres' <c>xmin</c> system column, used as the optimistic concurrency token.
    /// </summary>
    /// <remarks>
    /// Mapped rather than stored: every row already has <c>xmin</c>, it changes on every
    /// update, and using it costs no column and cannot get out of step with the row the way
    /// a hand-maintained version number can. It is what makes two registers selling the
    /// last unit collide loudly (Phase 3.6) instead of one overwriting the other's count.
    /// <para>
    /// <c>uint</c> specifically: the Npgsql convention that maps a property onto
    /// <c>xmin</c> matches on the type, on <c>ValueGeneratedOnAddOrUpdate</c> and on the
    /// property being a concurrency token. Changing the type silently turns this back into
    /// an ordinary column.
    /// </para>
    /// </remarks>
    public uint RowVersion { get; set; }

    /// <summary>Whether this product has reached the point where it should be reordered.</summary>
    /// <remarks>
    /// At the reorder point counts as below it: a reorder point of 10 means "order more
    /// when you are down to 10", not "when you are down to 9".
    /// </remarks>
    public bool IsBelowReorderPoint => ReorderPoint is { } point && OnHand <= point;
}
