using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// One line of a cart, as the engine needs it: already resolved against the catalog.
/// </summary>
/// <remarks>
/// Everything here is passed <i>in</i>. The engine reads no database and no clock, so pricing
/// can be tested exhaustively without either — which is the point of it living in
/// <c>Pos.Core</c>. The caller looks the product up, applies the tenant's tax class, checks
/// the caller's permission to override a price, and hands the result over.
/// <para>
/// <see cref="Description"/> travels with the line because it is <b>snapshotted</b> onto the
/// sale line: a report must never join back to the current product name any more than to the
/// current price. See CLAUDE.md invariant 5.
/// </para>
/// </remarks>
/// <param name="ProductId">The product being sold. The engine only carries it through.</param>
/// <param name="Description">The product's name as of now, for the snapshot and the receipt.</param>
/// <param name="Quantity">
/// How many, or how much. A <see cref="decimal"/> and not <see cref="Money"/>: weighed goods
/// are ordinary — 0.350 kg of cheese — and a quantity is not a currency.
/// </param>
/// <param name="UnitPrice">
/// The price to use, which is the catalog price unless the caller supplied an override and
/// held <c>CanOverridePrice</c>. The engine does not know the difference and must not: whether
/// an override was permitted is an authorization question, decided before this point.
/// </param>
/// <param name="TaxRate">
/// The tax class's rate as a fraction — <c>0.2300</c> is 23%. Per line, not per cart, so a
/// zero-rated item and a standard-rated one in the same basket are each taxed correctly.
/// </param>
/// <param name="LineDiscount">
/// An absolute amount off this line, never a percentage. A percentage applied to a rounded
/// line is a second place the client and the server can disagree about a total, so the client
/// computes the amount and sends it.
/// </param>
/// <param name="IsPriceOverridden">Carried through to the sale line for the audit trail.</param>
public sealed record CartLine(
    Guid ProductId,
    string Description,
    decimal Quantity,
    Money UnitPrice,
    decimal TaxRate,
    Money LineDiscount,
    bool IsPriceOverridden = false);
