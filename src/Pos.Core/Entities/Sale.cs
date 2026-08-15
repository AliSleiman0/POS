using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// The financial record. <b>Once <see cref="SaleStatus.Completed"/>, never updated or
/// deleted.</b>
/// </summary>
/// <remarks>
/// CLAUDE.md invariant 4. A refund is a new row of <see cref="SaleType.Refund"/> linked by
/// <see cref="OriginalSaleId"/>; a void is a status flag plus compensating stock movements.
/// The only columns a completed sale ever gains are the four void ones, and a test reads
/// every money column before and after a void to prove it.
/// <para>
/// The amounts here are the <b>only</b> values in the system rounded to the payable scale,
/// and the pricing engine rounds them once. They satisfy
/// <c>Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment</c> exactly.
/// </para>
/// </remarks>
public sealed class Sale : TenantEntity
{
    public const int VoidReasonMaxLength = 200;
    public const int RefundReasonMaxLength = 200;

    /// <summary>
    /// Per-tenant, sequential, and the reference a human quotes. Assigned inside the sale's
    /// transaction from a counter row, never <c>MAX()+1</c>.
    /// </summary>
    /// <remarks>
    /// Staff and auditors need something they can read down a phone; a GUID is not that.
    /// Gaps are suspicious in an audit, so the counter is not incremented speculatively —
    /// a rolled-back sale rolls back its own increment, which a Postgres sequence would not.
    /// </remarks>
    public long SaleNumber { get; set; }

    /// <summary>
    /// The client's GUID for this cart, generated <b>before its first attempt</b> and unique
    /// per tenant.
    /// </summary>
    /// <remarks>
    /// The domain half of idempotency, and it stands on its own: "exactly one sale for this
    /// cart" stays true even if the <c>IdempotencyRecord</c> table were dropped, because the
    /// guarantee is a unique index rather than a check-then-insert. Phase 9's offline outbox
    /// reconciles against this column.
    /// </remarks>
    public Guid ClientTransactionId { get; set; }

    /// <summary>The till this was rung through. Must be the shift's register.</summary>
    public Guid RegisterId { get; set; }

    /// <summary>
    /// The open shift the money went into. Required: without one there is nothing to
    /// reconcile the drawer against, and "we're short" becomes unanswerable.
    /// </summary>
    public Guid ShiftId { get; set; }

    /// <summary>Who served the customer.</summary>
    public Guid CashierId { get; set; }

    /// <inheritdoc cref="Entities.SaleType" />
    public SaleType Type { get; set; } = SaleType.Sale;

    /// <inheritdoc cref="Entities.SaleStatus" />
    public SaleStatus Status { get; set; } = SaleStatus.Completed;

    /// <summary>
    /// The tenant's tax mode <b>as of this sale</b>.
    /// </summary>
    /// <remarks>
    /// A snapshot, like every other historical amount, and for the same reason: a report that
    /// needs to know whether <see cref="Subtotal"/> is net of tax must not read
    /// <c>tenant.tax_mode</c>, because "refused once sales exist" is an API rule and anyone
    /// with SQL access can step around it. One cheap column makes the row self-describing.
    /// </remarks>
    public TaxMode TaxMode { get; set; }

    /// <summary>The sale this one reverses, for a <see cref="SaleType.Refund"/>.</summary>
    public Guid? OriginalSaleId { get; set; }

    /// <summary>Net of tax, before discounts.</summary>
    public Money Subtotal { get; set; }

    /// <summary>Line discounts plus the cart discount, net of tax.</summary>
    public Money DiscountTotal { get; set; }

    public Money TaxTotal { get; set; }

    /// <summary>
    /// What cash rounding moved the total by. Recorded, never absorbed — otherwise the drawer
    /// is over or short by an amount nothing in the system explains.
    /// </summary>
    public Money RoundingAdjustment { get; set; }

    /// <summary>What the customer paid. Negative on a refund.</summary>
    public Money Total { get; set; }

    /// <summary>
    /// Cash left over and above <see cref="Total"/>, for the staff.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately not part of <see cref="Total"/>.</b> The total is what the goods came to,
    /// and every report, tax figure and refund calculation reads it — folding a tip in would
    /// inflate revenue, inflate the tax owed on revenue that was never charged tax, and make a
    /// refund of the meal offer to return the tip as well.
    /// <para>
    /// <b>It is why DATA-MODEL invariant 2 now reads
    /// <c>sum(Tender.Amount) &gt;= Total + TipAmount</c>.</b> The product is cash-only, so a tip
    /// is physically cash in the drawer: the till really did receive €25 against a €20 bill, and
    /// €0 really did go back. Without a column for it the money still arrives — expected cash
    /// sums tendered less change given — but nothing on any row says why the drawer holds more
    /// than the takings, and every tipped shift reads as an unexplained surplus. That is the same
    /// failure as absorbing a cash-rounding adjustment instead of recording it.
    /// </para>
    /// <para>
    /// Not gated on <see cref="ServiceMode"/>: a tip jar at a counter is the same fact. Always
    /// zero or positive, and always zero on a <see cref="SaleType.Refund"/> — a shop does not
    /// take a tip for handing money back.
    /// </para>
    /// </remarks>
    public Money TipAmount { get; set; }

    /// <summary>
    /// When the sale was completed — when the customer stood at the counter.
    /// </summary>
    /// <remarks>
    /// Server-set from <c>TimeProvider</c> for an online sale. An offline sale supplies it as
    /// <c>occurredAt</c>, because it happened when the customer stood there and is written
    /// when the till reconnects, and <b>"what did we take on Tuesday" means the first of
    /// those</b> — the trading-day bounds, the Z-report and the shift's takings all read this
    /// column. Bounded by <see cref="Sales.OfflineSaleRules"/> before it is accepted; see
    /// <see cref="RecordedAt"/> for the other half of the pair.
    /// </remarks>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>
    /// When the server actually wrote the row. Always server-set, never client-supplied.
    /// </summary>
    /// <remarks>
    /// Equal to <see cref="CompletedAt"/> for a sale rung online, and later than it for one
    /// that was queued offline and replayed. <b>Both are needed, and neither substitutes for
    /// the other.</b> <see cref="CompletedAt"/> answers "when did this trade happen", which is
    /// the reporting question; this one answers "when did we learn about it", which is the
    /// reconciliation question — a drawer counted at 18:00 cannot be explained by a sale the
    /// server first saw at 21:00, and without this column nothing on the row says so.
    /// <para>
    /// Non-null on every row, including those written before the column existed: the migration
    /// backfills it from <see cref="CompletedAt"/>, which is exactly right for sales taken
    /// when there was no offline path at all.
    /// </para>
    /// </remarks>
    public DateTimeOffset RecordedAt { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedBy { get; set; }

    /// <summary>Required when voiding. A void with no reason is the record you will want and not have.</summary>
    public string? VoidReason { get; set; }

    /// <summary>Required when refunding, and stored on the refund row rather than the original.</summary>
    public string? RefundReason { get; set; }
}
