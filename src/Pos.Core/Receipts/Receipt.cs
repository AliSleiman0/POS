using Pos.Core.Entities;
using Pos.Core.Monetary;

namespace Pos.Core.Receipts;

/// <summary>
/// The shop's own details, as they head a receipt.
/// </summary>
/// <remarks>
/// Every field but the name is optional, because a tenant that has filled none of them in must
/// still be able to print. A renderer omits the lines it has no content for rather than
/// printing empty ones.
/// </remarks>
public sealed record ReceiptShop(
    string Name,
    string? AddressLine,
    string? TaxNumber,
    string? Header,
    string? Footer,
    string CurrencyCode);

/// <summary>One line of the sale, exactly as it was rung.</summary>
/// <param name="Discount">This line's discount including its share of any cart discount, net of tax.</param>
/// <param name="LineTotal">What this line contributed to the amount paid.</param>
public sealed record ReceiptLine(
    int LineNumber,
    string Description,
    decimal Quantity,
    Money UnitPrice,
    decimal TaxRate,
    Money Discount,
    Money LineTotal,
    bool IsPriceOverridden);

/// <summary>
/// One rate's worth of tax.
/// </summary>
/// <remarks>
/// A legal requirement in most jurisdictions, and <b>not derivable from a single tax total</b>:
/// a basket of zero-rated bread and standard-rated wine has one <c>TaxTotal</c> and two rates
/// behind it, and it is the split a VAT return is filed on.
/// </remarks>
/// <param name="Rate">The rate as a fraction — 0.2300 prints as 23%.</param>
/// <param name="NetAmount">The taxable amount at this rate, after discounts, net of tax.</param>
/// <param name="TaxAmount">The tax on it.</param>
/// <param name="GrossAmount"><see cref="NetAmount"/> + <see cref="TaxAmount"/>.</param>
public sealed record ReceiptTaxLine(
    decimal Rate,
    Money NetAmount,
    Money TaxAmount,
    Money GrossAmount);

/// <summary>One payment, and the change given against it.</summary>
public sealed record ReceiptTender(TenderMethod Method, Money Amount, Money? ChangeGiven);

/// <summary>
/// A rendered receipt: one payload, three consumers.
/// </summary>
/// <remarks>
/// <b>Built server-side, never composed in a browser.</b> Browser printing renders this today,
/// a thermal printer through the desktop app will render it later, and email later still.
/// Three independent renderers would guarantee three subtly different receipts, and the one a
/// tax authority looks at would be the wrong one.
/// <para>
/// Times are in the <b>tenant's</b> zone, resolved by <see cref="ReceiptBuilder"/>. Storage and
/// transit stay UTC (invariant 8); a receipt is the edge, and 23:15 printed as 22:15 is a
/// dispute about which day something was bought on.
/// </para>
/// </remarks>
/// <param name="Kind">What this receipt is for. See <see cref="ReceiptKind"/>.</param>
/// <param name="CompletedAtLocal">When the sale happened, in the tenant's zone.</param>
/// <param name="IssuedAtLocal">
/// When this payload was rendered, in the tenant's zone. What a reprint is stamped with — the
/// mark that stops a duplicate receipt being presented as a second proof of purchase.
/// </param>
/// <param name="TimeZoneId">The zone the two timestamps above are in, so a renderer can say so.</param>
/// <param name="OriginalSaleNumber">The sale a refund reverses.</param>
/// <param name="TaxBreakdown">
/// By rate, ascending, and the parts sum to <paramref name="TaxTotal"/> exactly. See
/// <see cref="ReceiptBuilder"/> for why that is arranged rather than assumed.
/// </param>
public sealed record Receipt(
    ReceiptKind Kind,
    ReceiptShop Shop,
    long SaleNumber,
    DateTimeOffset CompletedAtLocal,
    DateTimeOffset IssuedAtLocal,
    string TimeZoneId,
    string CashierName,
    string RegisterName,
    TaxMode TaxMode,
    long? OriginalSaleNumber,
    string? VoidReason,
    string? RefundReason,
    IReadOnlyList<ReceiptLine> Lines,
    IReadOnlyList<ReceiptTaxLine> TaxBreakdown,
    IReadOnlyList<ReceiptTender> Tenders,
    Money Subtotal,
    Money DiscountTotal,
    Money TaxTotal,
    Money RoundingAdjustment,
    Money Total,
    Money ChangeGiven);
