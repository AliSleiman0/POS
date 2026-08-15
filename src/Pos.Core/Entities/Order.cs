using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A table's or a tab's running order. <b>Working state, not a financial record.</b>
/// </summary>
/// <remarks>
/// <b>This is the entity the phase's whole design rests on.</b> An order is mutable for as long
/// as it is open: lines are added over an hour, quantities change, items are voided, the whole
/// thing moves to another table. None of that is allowed of a <see cref="Sale"/>, and none of it
/// needs to be — because an order is not money. When a bill is settled, an ordinary
/// <see cref="Sale"/> is committed through <c>ISaleWriter</c>, priced by the same engine the
/// retail till uses, and <i>that</i> row is append-only. CLAUDE.md invariant 4 is untouched
/// rather than weakened.
/// <para>
/// <b>The link runs one way.</b> <c>OrderBill.SaleId</c> points from here to the sale; there is
/// no <c>OrderId</c> on <see cref="Sale"/>. That is what keeps <c>DECISIONS.md</c>'s
/// "restaurant mode is a separate model, not a bolt-on to <c>Sale</c>" literally true: the retail
/// path compiles, queries and reports exactly as it did, unaware this entity exists.
/// </para>
/// <para>
/// <b>The database table is <c>customer_order</c>.</b> <c>ORDER</c> is a reserved word in SQL, so
/// a table named for it works only while every hand-written query remembers to quote it — and
/// this codebase has hand-written queries in the money paths, because EF cannot aggregate a
/// value-converted <c>Money</c>. Phase 10.8's reporting will add more.
/// </para>
/// </remarks>
public sealed class Order : TenantEntity
{
    public const int TabNameMaxLength = 60;
    public const int NoteMaxLength = 500;
    public const int AbandonReasonMaxLength = 200;

    /// <summary>
    /// Per-tenant, sequential, and what staff shout across a room.
    /// </summary>
    /// <remarks>
    /// Assigned from <see cref="OrderSequence"/> inside the opening transaction, on exactly the
    /// reasoning <see cref="Sale.SaleNumber"/> documents. It is <b>not</b> a sale number and the
    /// two series are independent: one order can produce three sales, and an abandoned order
    /// produces none, so sharing a counter would put unexplained gaps in the financial series —
    /// which is the one thing that counter exists to avoid.
    /// </remarks>
    public long OrderNumber { get; set; }

    /// <inheritdoc cref="Entities.OrderType" />
    public OrderType Type { get; set; } = OrderType.Table;

    /// <inheritdoc cref="Entities.OrderStatus" />
    public OrderStatus Status { get; set; } = OrderStatus.Open;

    /// <summary>The table being served, for <see cref="OrderType.Table"/>. Null otherwise.</summary>
    public Guid? DiningTableId { get; set; }

    /// <summary>
    /// What a bar calls this tab — "Sarah, red coat". Required for <see cref="OrderType.Tab"/>.
    /// </summary>
    /// <remarks>
    /// Free text and not a customer record. A bar tab is found by looking at the person, and
    /// making staff create a customer to pour a pint would mean they stop using the feature.
    /// </remarks>
    public string? TabName { get; set; }

    /// <summary>
    /// The till the order was opened at, when there was one.
    /// </summary>
    /// <remarks>
    /// Informational, and deliberately <b>not</b> what the eventual sale is booked to: the money
    /// goes through whichever register takes the payment, into that register's open shift. A
    /// table opened on the terrace handheld and settled at the bar belongs to the bar's drawer,
    /// because that is where the cash physically is. Nullable because a manager can open a tab
    /// from a back-office browser, which carries no register claim.
    /// </remarks>
    public Guid? RegisterId { get; set; }

    /// <summary>Who opened it. From the validated token, like every other actor in this system.</summary>
    public Guid OpenedBy { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>
    /// How many people are eating.
    /// </summary>
    /// <remarks>
    /// The denominator of "average spend per cover", which is the number a restaurant owner
    /// actually manages by. Nullable because a takeaway has none and guessing one would quietly
    /// corrupt that average.
    /// </remarks>
    public int? CoverCount { get; set; }

    /// <summary>A note for the floor — an allergy, a birthday, "waiting on a friend".</summary>
    public string? Note { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    public Guid? ClosedBy { get; set; }

    /// <summary>Required when abandoning. A walk-out with no reason is the record you will want.</summary>
    public string? AbandonReason { get; set; }
}
