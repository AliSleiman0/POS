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
    /// When the sale was completed, server-set from <c>TimeProvider</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <c>CreatedAt</c> for the case Phase 9 creates: an offline sale happened
    /// when the customer stood there and is written when the till reconnects, and "what did
    /// we take on Tuesday" means the first of those.
    /// </remarks>
    public DateTimeOffset CompletedAt { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedBy { get; set; }

    /// <summary>Required when voiding. A void with no reason is the record you will want and not have.</summary>
    public string? VoidReason { get; set; }

    /// <summary>Required when refunding, and stored on the refund row rather than the original.</summary>
    public string? RefundReason { get; set; }
}
