using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// One priced line, at full precision, ready to be snapshotted onto a <c>SaleLine</c>.
/// </summary>
/// <remarks>
/// <b>Nothing here is rounded to the payable scale.</b> These are stored at
/// <see cref="Rounding.StorageScale"/>; only the sale header is taken to two places, once.
/// Rounding each line and summing would give a total a cent or two from the honest one — the
/// bug CLAUDE.md invariant 3 exists to prevent, and the reason a Z-report would never balance.
/// </remarks>
/// <param name="Source">The cart line this came from, carrying the fields to snapshot.</param>
/// <param name="LineNumber">1-based, in cart order. What the receipt prints.</param>
/// <param name="Gross">
/// <c>Quantity × UnitPrice</c> before any discount. In <c>Inclusive</c> mode this still
/// contains tax, which is what makes <see cref="Subtotal"/> different from it.
/// </param>
/// <param name="Subtotal">
/// The line's contribution to <c>Sale.Subtotal</c>: net of tax in both modes, so the sale-level
/// identity holds however the tenant prices.
/// </param>
/// <param name="Discount">
/// This line's total discount, net of tax, including its apportioned share of any cart
/// discount. What <c>SaleLine.DiscountAmount</c> stores.
/// </param>
/// <param name="CartDiscountShare">
/// The part of <see cref="Discount"/> that came from the cart rather than the line. Kept
/// separately because the apportionment is the thing worth being able to check.
/// </param>
/// <param name="Tax">Tax on the discounted amount, never on the pre-discount amount.</param>
/// <param name="Total">
/// What this line contributes to the amount payable. In <c>Exclusive</c> mode that is the
/// discounted net plus tax; in <c>Inclusive</c> mode the discounted gross, which already
/// contains it.
/// </param>
public sealed record PricedLine(
    CartLine Source,
    int LineNumber,
    Money Gross,
    Money Subtotal,
    Money Discount,
    Money CartDiscountShare,
    Money Tax,
    Money Total);
