namespace Pos.Core.Entities;

/// <summary>Cash entering or leaving a drawer other than through a sale.</summary>
/// <remarks>
/// Every one of these changes what the drawer should contain at close, so an unrecorded one
/// shows up as a variance nobody can explain — which is the same as the shift arithmetic
/// being wrong, from the point of view of the person holding the cash.
/// </remarks>
public enum CashMovementType
{
    /// <summary>Cash moved to the safe mid-shift, so a till is not holding the day's takings.</summary>
    Drop = 0,

    /// <summary>Cash paid out of the drawer — a supplier at the door, a delivery driver.</summary>
    Payout = 1,

    /// <summary>Small expenses taken from the till: postage, milk, parking.</summary>
    PettyCash = 2,

    /// <summary>
    /// A correction to the float. The only type whose sign is unconstrained, because a
    /// correction can go either way by definition.
    /// </summary>
    Correction = 3,
}
