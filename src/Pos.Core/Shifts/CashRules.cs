using Pos.Core.Entities;

namespace Pos.Core.Shifts;

/// <summary>
/// What a cash movement has to look like before anything writes it.
/// </summary>
/// <remarks>
/// The sibling of <c>StockRules</c>, and it exists for the same reason: no check constraint
/// can police a sign against a type, because both directions are perfectly good numbers for
/// the column. A drop entered as a positive is a drawer wrong by twice the amount, in the
/// direction nobody notices until close.
/// </remarks>
public static class CashRules
{
    /// <summary>
    /// Whether <paramref name="amount"/>'s sign agrees with <paramref name="type"/>.
    /// </summary>
    /// <remarks>
    /// Drops, payouts and petty cash all take money <i>out</i> of the drawer, so they are
    /// negative. A correction is the one type whose direction is unconstrained — correcting a
    /// float either way is the whole point of it — and it may not be zero, because a
    /// correction that corrects nothing is a row with no meaning.
    /// </remarks>
    public static bool IsSignConsistent(CashMovementType type, decimal amount) => type switch
    {
        CashMovementType.Drop => amount < 0m,
        CashMovementType.Payout => amount < 0m,
        CashMovementType.PettyCash => amount < 0m,
        CashMovementType.Correction => amount != 0m,
        _ => false,
    };

    /// <summary>The message that says which direction was expected, and why.</summary>
    public static string SignMessage(CashMovementType type) => type switch
    {
        CashMovementType.Drop =>
            "A drop moves cash to the safe, so its amount must be less than zero.",
        CashMovementType.Payout =>
            "A payout takes cash out of the drawer, so its amount must be less than zero.",
        CashMovementType.PettyCash =>
            "Petty cash leaves the drawer, so its amount must be less than zero.",
        _ =>
            "A correction must move something, so its amount cannot be zero.",
    };

    /// <summary>Every type a client may send. All of them, unlike stock movements.</summary>
    /// <remarks>
    /// There is no equivalent of <c>StockMovementType.Sale</c> here — no cash movement type is
    /// written by the system on a caller's behalf, so none of them has to be withheld.
    /// </remarks>
    public static IReadOnlyList<CashMovementType> AllowedTypes { get; } =
        [.. Enum.GetValues<CashMovementType>()];
}
