using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// A fully priced cart: the sale header's amounts, and the lines behind them.
/// </summary>
/// <remarks>
/// The header amounts are the <b>only</b> values in the system rounded to the payable scale,
/// and they are rounded once. <c>POST /sales</c> and <c>POST /sales/quote</c> both return
/// these, computed by the same call — which is what makes "a quote returns exactly what a sale
/// would compute" a mechanical fact rather than a promise.
/// </remarks>
/// <param name="Lines">Priced lines at full precision, in cart order.</param>
/// <param name="Subtotal">
/// Net of tax and before discounts. <b>Derived</b> from the other three rather than summed
/// independently — see <see cref="PricingEngine"/>, where the reason is argued.
/// </param>
/// <param name="DiscountTotal">Line discounts plus the cart discount, net of tax.</param>
/// <param name="TaxTotal">Tax on the discounted amounts.</param>
/// <param name="RoundingAdjustment">
/// What cash rounding moved the total by, recorded rather than absorbed. Zero unless the
/// tenant has a smallest-coin rule.
/// </param>
/// <param name="Total">
/// The amount a person pays, after cash rounding. Satisfies
/// <c>Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment</c> exactly, on these
/// stored two-decimal values, in both tax modes — which is DATA-MODEL.md invariant 1.
/// </param>
public sealed record PricedSale(
    IReadOnlyList<PricedLine> Lines,
    Money Subtotal,
    Money DiscountTotal,
    Money TaxTotal,
    Money RoundingAdjustment,
    Money Total);
