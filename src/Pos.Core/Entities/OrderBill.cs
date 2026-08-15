using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>Where a bill is between being asked for and being paid.</summary>
public enum OrderBillStatus
{
    /// <summary>Printed and waiting. Its lines can still be reallocated.</summary>
    Open = 0,

    /// <summary>Settled. <see cref="OrderBill.SaleId"/> names the sale it became.</summary>
    Paid = 1,

    /// <summary>Torn up before payment — a split somebody changed their mind about.</summary>
    Voided = 2,
}

/// <summary>
/// One bill against an order: a share of its lines, settled as an ordinary <see cref="Sale"/>.
/// </summary>
/// <remarks>
/// <b>This is where the restaurant model meets the money, and the arrow points one way.</b>
/// <see cref="SaleId"/> is set when the bill is paid; there is no <c>OrderId</c> on
/// <see cref="Sale"/>. The retail path compiles, queries and reports exactly as it did, unaware
/// any of this exists — which is what makes <c>DECISIONS.md</c>'s "restaurant mode is a separate
/// model, not a bolt-on to <c>Sale</c>" literally true rather than approximately.
/// <para>
/// <b>Splitting evenly does not produce bills.</b> Four people paying a quarter each is four
/// cash tenders against one sale, which <c>Tender</c> has supported since Phase 3. Splitting by
/// allocating a quarter of every line to four bills would put fractional quantities through four
/// independent pricings, and four independently rounded parts do not sum to the whole — the
/// penny-off bug <c>DiscountApportionment</c> exists to prevent, reintroduced one level up.
/// Bills are for splitting <i>by item or by seat</i>, where the quantities are whole.
/// </para>
/// </remarks>
public sealed class OrderBill : TenantEntity
{
    public Guid OrderId { get; set; }

    /// <summary>1-based within the order. What a waiter says: "bill two is the card one".</summary>
    public int BillNumber { get; set; }

    /// <inheritdoc cref="OrderBillStatus" />
    public OrderBillStatus Status { get; set; } = OrderBillStatus.Open;

    /// <summary>
    /// This bill's idempotency identity, minted when the bill is created.
    /// </summary>
    /// <remarks>
    /// <b>Minted with the bill, not with the payment attempt</b>, which is invariant 6's whole
    /// point: a till that generated a key per attempt would charge a table twice on a retry. It
    /// is the <c>clientTransactionId</c> of the sale this becomes, so "did bill two go through?"
    /// is answerable with <c>GET /sales/by-client-transaction/{id}</c> rather than by trying the
    /// payment again to find out.
    /// </remarks>
    public Guid ClientTransactionId { get; set; }

    /// <summary>The sale this became. Null until it is paid.</summary>
    public Guid? SaleId { get; set; }

    /// <summary>What was left for the staff on this bill. See <see cref="Sale.TipAmount"/>.</summary>
    public Money TipAmount { get; set; }

    public DateTimeOffset? PaidAt { get; set; }

    public Guid? PaidBy { get; set; }
}

/// <summary>One order line's share of a bill.</summary>
/// <remarks>
/// <b>A quantity, not a flag</b>, because a shared bottle is real: two of four people split a
/// wine and each takes half. The allocations across a line's bills are validated to sum to at
/// most the line's own quantity, and an order cannot close until they sum to exactly it — food
/// nobody was billed for is food given away with nothing on any row explaining it.
/// </remarks>
public sealed class OrderBillLine : TenantEntity
{
    public Guid OrderBillId { get; set; }

    public Guid OrderLineId { get; set; }

    /// <summary>How much of the line this bill takes. Positive.</summary>
    public decimal Quantity { get; set; }
}
