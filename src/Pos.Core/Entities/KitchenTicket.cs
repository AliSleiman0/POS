using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// What one station was told to cook, in one round. <b>Append-only.</b>
/// </summary>
/// <remarks>
/// <b>This is a record of an instruction, not a view over the order.</b> Everything a kitchen
/// needs to read is snapshotted onto it and its lines, and none of it is refreshed afterwards.
/// That is the whole design decision, and it is the same one <see cref="SaleLine"/> makes for a
/// different reason: a line voided after firing is food that was already cooked, and a ticket that
/// re-read the order would quietly erase the evidence that the shop lost a steak. The void is
/// shown <i>against</i> the ticket, never <i>in</i> it.
/// <para>
/// <b>One ticket per station per course per fire.</b> A single ticket holding the whole table is
/// useless to a kitchen — the grill would read past three drinks to find its steak — and a ticket
/// spanning two courses is a queue the pass cannot pace. Firing course 1 for a table with food and
/// drink produces two tickets; firing course 2 later produces more, and the earlier ones are not
/// touched.
/// </para>
/// <para>
/// <b>Nothing here is money.</b> A ticket has no prices, no totals and no tax, because a kitchen
/// does not charge anybody — the amounts are decided when a bill is settled, from the order line's
/// snapshots, by the one pricing engine. See <see cref="Order"/>.
/// </para>
/// </remarks>
public sealed class KitchenTicket : TenantEntity
{
    public const int OrderLabelMaxLength = 60;

    /// <summary>The order it came off. Provenance, and how the floor screen finds it back.</summary>
    public Guid OrderId { get; set; }

    /// <summary>The station it was addressed to.</summary>
    public Guid StationId { get; set; }

    /// <summary>The round it belongs to, copied from the lines on it. All of them share it.</summary>
    public int Course { get; set; }

    /// <summary>
    /// The order's number, snapshotted so the display reads one table.
    /// </summary>
    /// <remarks>
    /// This is the number staff shout across a room, and it is on the ticket rather than joined to
    /// because a kitchen display polls every few seconds and a join per ticket per poll is a query
    /// the busiest minute of the night cannot afford.
    /// </remarks>
    public long OrderNumber { get; set; }

    /// <summary>
    /// Where it is going, as a person reads it — "Table 12", "Sarah, red coat", "Takeaway".
    /// </summary>
    /// <remarks>
    /// A snapshot, and deliberately so: a table renamed at midnight must not rewrite what the
    /// grill was told at eight. It is also why this is text rather than a table id — a takeaway
    /// has no table at all and the kitchen still needs to be told something.
    /// </remarks>
    public required string OrderLabel { get; set; }

    /// <summary>When the pass was told. What the display's elapsed timer counts from.</summary>
    public DateTimeOffset FiredAt { get; set; }

    /// <summary>Who fired it. From the validated token, like every other actor here.</summary>
    public Guid FiredBy { get; set; }

    /// <inheritdoc cref="Entities.KitchenTicketStatus" />
    public KitchenTicketStatus Status { get; set; } = KitchenTicketStatus.Active;

    /// <summary>When the kitchen cleared it. Null while it is still <see cref="KitchenTicketStatus.Active"/>.</summary>
    public DateTimeOffset? BumpedAt { get; set; }

    /// <summary>Who cleared it.</summary>
    public Guid? BumpedBy { get; set; }
}
