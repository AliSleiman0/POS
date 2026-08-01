using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One movement of stock. Append-only: the ledger is the truth and
/// <see cref="StockItem.OnHand"/> is a cache of it.
/// </summary>
/// <remarks>
/// <b>Never updated, never deleted</b> (CLAUDE.md invariant 4). A movement written in error
/// is corrected by a compensating movement, which is what makes the correction itself
/// visible — the question staff actually have is never "what is on hand" but "why is on hand
/// wrong?", and a mutable number cannot answer it.
/// <para>
/// The inherited <c>UpdatedAt</c> and <c>UpdatedBy</c> therefore stay null for the row's
/// whole life. They are not removed because <see cref="TenantEntity"/> is what registers an
/// entity for tenant scoping, and a bespoke base class for one table would be a worse trade.
/// </para>
/// <para>
/// This is what makes Phase 9's offline reconciliation tractable: replaying queued movements
/// against a ledger works, whereas reconciling two counters that disagree does not.
/// </para>
/// </remarks>
public sealed class StockMovement : TenantEntity
{
    /// <summary>Longest reason accepted. Enough for a sentence, short of an essay in a grid cell.</summary>
    public const int ReasonMaxLength = 200;

    public Guid ProductId { get; set; }

    /// <summary>Why it moved. See <see cref="StockMovementType"/>.</summary>
    public StockMovementType Type { get; set; }

    /// <summary>
    /// <b>Signed</b>, <c>numeric(19,4)</c>: positive into the shop, negative out of it.
    /// </summary>
    /// <remarks>
    /// Signed rather than a magnitude plus a direction implied by <see cref="Type"/>, so that
    /// rebuilding on-hand is a <c>SUM</c> and not a fold that has to know what every type
    /// means. Quantities are fractional because 0.350 kg of cheese is an ordinary sale.
    /// </remarks>
    public decimal Quantity { get; set; }

    /// <summary>
    /// Why, in words. Required by <c>POST /stock/adjustments</c>, absent on a sale.
    /// </summary>
    /// <remarks>
    /// Nullable in the column but mandatory at the endpoint, and the split is deliberate: a
    /// sale's reason is the sale, and forcing prose onto it would produce a table full of the
    /// word "sale". A manual adjustment with no reason is exactly the record you need six
    /// months later and will not have.
    /// </remarks>
    public string? Reason { get; set; }

    /// <summary>The sale that caused this, from Phase 3. Null for a manual adjustment.</summary>
    public Guid? SaleId { get; set; }

    /// <summary>
    /// Who moved it, or <see langword="null"/> when the system did.
    /// </summary>
    /// <remarks>
    /// Overlaps <c>CreatedBy</c> and is kept anyway. The actor is part of what this row
    /// <i>means</i> — a stock investigation asks who wrote off the missing six — whereas the
    /// audit columns are metadata every table carries and may be reshaped for reasons that
    /// have nothing to do with inventory.
    /// </remarks>
    public Guid? PerformedBy { get; set; }

    /// <summary>
    /// When it moved, in UTC. Server-set from <c>TimeProvider</c>, never client-supplied.
    /// </summary>
    /// <remarks>
    /// Separate from <c>CreatedAt</c> for the case Phase 9 creates: an offline movement is
    /// recorded when it happened and written when the till reconnects, and the report that
    /// asks "what moved on Tuesday" means the first of those.
    /// </remarks>
    public DateTimeOffset OccurredAt { get; set; }
}
