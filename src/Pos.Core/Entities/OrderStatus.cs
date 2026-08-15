namespace Pos.Core.Entities;

/// <summary>Where an order is in its life.</summary>
/// <remarks>
/// <b>Three states, and the third is the one that matters.</b> An order that is walked out on, or
/// opened on the wrong table and given up, has to go somewhere — and it must not be
/// <see cref="Closed"/>, because closed means "settled" and a report that counted an abandoned
/// order as settled would claim takings nobody received. Deleting it instead would leave the
/// kitchen tickets it fired pointing at nothing.
/// </remarks>
public enum OrderStatus
{
    /// <summary>Being served. Lines can be added, amended and voided.</summary>
    Open = 0,

    /// <summary>
    /// Every line is on a bill and every bill is paid. The order is history.
    /// </summary>
    /// <remarks>
    /// Reached only through payment, and only when nothing is left unbilled — an order closed
    /// with lines nobody paid for is food given away that no report would ever show.
    /// </remarks>
    Closed = 1,

    /// <summary>
    /// Given up on: a walk-out, or a mistake. Requires a reason and is audited.
    /// </summary>
    /// <remarks>
    /// Deliberately not a kind of <see cref="Closed"/>. Whatever was fired was cooked and is
    /// gone, and the shop needs that visible as a loss rather than folded into a day's takings.
    /// </remarks>
    Abandoned = 2,
}
