using Pos.Core.Entities;
using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// Everything the pricing engine needs, and nothing it does not.
/// </summary>
/// <remarks>
/// The tenant's settings arrive as plain values rather than as a <c>Tenant</c>, so the engine
/// cannot accidentally reach for a field that would make it depend on the database. It is a
/// pure function of this record.
/// </remarks>
/// <param name="Lines">The lines, in the order they will be numbered on the sale.</param>
/// <param name="CartDiscount">
/// An absolute amount off the whole basket. <b>Apportioned across the lines</b> rather than
/// subtracted from the total — see <see cref="DiscountApportionment"/> for why that is not an
/// implementation detail.
/// </param>
/// <param name="TaxMode">The tenant's mode. Decides what a stored price <i>means</i>.</param>
/// <param name="CashRoundingIncrement">
/// The tenant's smallest coin, or zero for no cash rounding. Applied last, and only to the
/// payable total.
/// </param>
public sealed record Cart(
    IReadOnlyList<CartLine> Lines,
    Money CartDiscount,
    TaxMode TaxMode,
    decimal CashRoundingIncrement = 0m);
