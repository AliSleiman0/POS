namespace Pos.Core.Entities;

/// <summary>Where one ordered item is between being keyed and being eaten.</summary>
/// <remarks>
/// <b>This tracks what the kitchen has been told, not what the kitchen has done.</b> How far
/// along a dish is belongs to the ticket the station is looking at
/// (<c>KitchenTicket</c>), because one line can appear on two stations' tickets and be ready at
/// one and not the other. What the order needs to know is narrower and it is here: may this line
/// still be edited, and did somebody already cook it.
/// </remarks>
public enum OrderLineStatus
{
    /// <summary>
    /// Keyed but not sent. Freely editable, and freely removable by anybody who can take orders.
    /// </summary>
    /// <remarks>
    /// Nothing has been cooked, so changing your mind costs the shop nothing — which is exactly
    /// why removing one of these needs no manager and no reason.
    /// </remarks>
    Pending = 0,

    /// <summary>
    /// Sent to a station. Still on the bill, but no longer free to cancel.
    /// </summary>
    /// <remarks>
    /// The line the authorization turns on: voiding one of these is food already being cooked,
    /// so it takes <c>CanVoidFiredLine</c>, a reason, and an audit entry.
    /// </remarks>
    Fired = 1,

    /// <summary>
    /// Cancelled. Kept on the order rather than deleted.
    /// </summary>
    /// <remarks>
    /// A deleted line is a question nobody can answer later — "why did the kitchen cook a steak
    /// that is on no bill?" The row stays, carrying who voided it and why, and it is simply
    /// excluded from every amount.
    /// </remarks>
    Voided = 2,
}
