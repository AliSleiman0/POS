namespace Pos.Core.Entities;

/// <summary>Where a ticket is in a station's queue.</summary>
/// <remarks>
/// Stored as text through <c>HasEnumAsText</c>, so these names are the values in
/// <c>ck_kitchen_ticket_status_allowed</c> and in every row already written. A wire contract, the
/// same way <see cref="OrderLineStatus"/> is.
/// <para>
/// <b>There is no <c>Voided</c> member.</b> A ticket is an append-only record of what the kitchen
/// was <i>told</i>, and voiding an order line afterwards does not unsay it — the food may already
/// be on a plate. The void is recorded on <see cref="OrderLine"/>, where the money is decided, and
/// the display shows it as a strike-through against a ticket that stays exactly as it was sent.
/// </para>
/// </remarks>
public enum KitchenTicketStatus
{
    /// <summary>On the station's screen and waiting to be cooked.</summary>
    Active,

    /// <summary>
    /// Cleared by the kitchen. Off the screen, still on the record.
    /// </summary>
    /// <remarks>
    /// Reversible — a ticket bumped by a sleeve is recalled, which is why this is a status rather
    /// than a delete. Never <i>needing</i> a recall is not a shape any kitchen has.
    /// </remarks>
    Bumped,
}
