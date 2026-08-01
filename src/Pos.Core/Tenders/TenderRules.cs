using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;

namespace Pos.Core.Tenders;

/// <summary>
/// What has to be true about the money offered against a sale, and how much goes back.
/// </summary>
/// <remarks>
/// Pure, and separate from the pricing engine on purpose: pricing decides what is owed, this
/// decides whether it has been paid. The two fail for different reasons and a cashier acts on
/// them differently — one is "that discount is wrong", the other is "I need another note".
/// </remarks>
public static class TenderRules
{
    /// <summary>
    /// Change due on a sale: the excess over <paramref name="total"/>.
    /// </summary>
    /// <param name="total">What the sale came to. Positive.</param>
    /// <param name="tenders">What was handed over.</param>
    /// <exception cref="UnderTenderException">The tenders do not cover the total.</exception>
    public static Money ChangeFor(Money total, IReadOnlyList<Money> tenders)
    {
        ArgumentNullException.ThrowIfNull(tenders);

        var offered = Money.Sum(tenders);

        if (offered < total)
        {
            throw new UnderTenderException(
                $"The sale comes to {total} and {offered} was tendered.");
        }

        // Over-tender is the normal case, not an exception to handle: a customer pays for
        // 18.45 with a 20 note far more often than they produce exact change.
        return offered - total;
    }

    /// <summary>Whether <paramref name="tenders"/> cover <paramref name="total"/>.</summary>
    /// <remarks>
    /// Offered alongside <see cref="ChangeFor"/> so a caller collecting several validation
    /// errors at once can ask without catching, which is how every endpoint in this codebase
    /// reports a bad request — all the fields, not the first one.
    /// </remarks>
    public static bool IsSufficient(Money total, IReadOnlyList<Money> tenders)
    {
        ArgumentNullException.ThrowIfNull(tenders);

        return Money.Sum(tenders) >= total;
    }

    /// <summary>
    /// Whether <paramref name="method"/> is one the MVP actually takes.
    /// </summary>
    /// <remarks>
    /// <see cref="TenderMethod"/> declares four values so that <c>Tender</c> stays a
    /// collection of rows carrying a discriminator — which is what keeps a future payment
    /// method additive rather than a migration on the financial table. Declaring a value is
    /// not supporting it, though: without this check a client could post a <c>Card</c> tender
    /// that no processor ever saw, and it would sit in the takings and reconcile against
    /// nothing.
    /// </remarks>
    public static bool IsAccepted(TenderMethod method) => method is TenderMethod.Cash;

    /// <summary>The methods a client may send today.</summary>
    public static IReadOnlyList<TenderMethod> AcceptedMethods { get; } =
        [.. Enum.GetValues<TenderMethod>().Where(IsAccepted)];

    /// <summary>
    /// Whether a tender's sign agrees with the direction of the sale.
    /// </summary>
    /// <remarks>
    /// A refund's tenders are negative, matching its negative total, so that
    /// <c>sum(Tender.Amount) == Sale.Total</c> on a refund and <c>&gt;=</c> on a sale is the
    /// whole of DATA-MODEL.md invariant 2 with no special case. A positive tender on a refund
    /// would make the shift's expected cash go up when money left the drawer — the arithmetic
    /// would be wrong by twice the refund, in the direction that looks like theft.
    /// </remarks>
    public static bool IsSignConsistent(SaleType type, Money amount) => type switch
    {
        SaleType.Sale => !amount.IsNegative,
        SaleType.Refund => !amount.IsPositive,
        _ => false,
    };
}
