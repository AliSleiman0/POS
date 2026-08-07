using Pos.Core.Entities;
using Pos.Core.Receipts;

namespace Pos.Api.Endpoints;

/// <summary>The shop's own details, as they head a receipt.</summary>
public sealed record ReceiptShopResponse(
    string Name,
    string? AddressLine,
    string? TaxNumber,
    string? Header,
    string? Footer,
    string CurrencyCode);

/// <summary>One line of the sale, from its snapshot and never from the catalog.</summary>
public sealed record ReceiptLineResponse(
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal Discount,
    decimal LineTotal,
    bool IsPriceOverridden);

/// <summary>One rate's worth of tax. The parts sum to <c>taxTotal</c> exactly.</summary>
public sealed record ReceiptTaxLineResponse(
    decimal Rate,
    decimal NetAmount,
    decimal TaxAmount,
    decimal GrossAmount);

/// <summary>One payment, and the change given against it.</summary>
public sealed record ReceiptTenderResponse(TenderMethod Method, decimal Amount, decimal? ChangeGiven);

/// <summary>
/// A receipt, rendered server-side into one payload for every consumer.
/// </summary>
/// <remarks>
/// <b>Amounts are <c>decimal</c>, not <c>Money</c></b> — every DTO in this API is, and
/// <c>SaleContractTests.No_endpoint_dto_declares_a_money_property</c> fails the build otherwise.
/// <para>
/// The two timestamps are in the <b>tenant's</b> zone, carrying its offset, which is why they
/// are named for it. Everything else in this API is UTC (invariant 8); a receipt is the edge
/// where a trading day is decided, and 23:15 printed as 22:15 is a dispute about which day
/// something was bought on.
/// </para>
/// </remarks>
/// <param name="IssuedAtLocal">
/// When this copy was rendered. A reprint is stamped with it — see docs/DECISIONS.md on why the
/// mark is applied by the client rather than counted by the server.
/// </param>
public sealed record ReceiptResponse(
    ReceiptKind Kind,
    ReceiptShopResponse Shop,
    Guid SaleId,
    long SaleNumber,
    DateTimeOffset CompletedAtLocal,
    DateTimeOffset IssuedAtLocal,
    string TimeZoneId,
    string CashierName,
    string RegisterName,
    TaxMode TaxMode,
    Guid? OriginalSaleId,
    long? OriginalSaleNumber,
    string? VoidReason,
    string? RefundReason,
    IReadOnlyList<ReceiptLineResponse> Lines,
    IReadOnlyList<ReceiptTaxLineResponse> TaxBreakdown,
    IReadOnlyList<ReceiptTenderResponse> Tenders,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal RoundingAdjustment,
    decimal Total,
    decimal ChangeGiven)
{
    /// <summary>Projects the Core payload onto the wire.</summary>
    /// <remarks>
    /// A straight field-for-field copy with the <c>Money</c> unwrapped. It does no arithmetic
    /// and must not start doing any: <see cref="ReceiptBuilder"/> is where a receipt's numbers
    /// are decided, and a second place that adjusted them would be a second answer.
    /// </remarks>
    public static ReceiptResponse From(Receipt receipt, Guid saleId, Guid? originalSaleId)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new ReceiptResponse(
            receipt.Kind,
            new ReceiptShopResponse(
                receipt.Shop.Name,
                receipt.Shop.AddressLine,
                receipt.Shop.TaxNumber,
                receipt.Shop.Header,
                receipt.Shop.Footer,
                receipt.Shop.CurrencyCode),
            saleId,
            receipt.SaleNumber,
            receipt.CompletedAtLocal,
            receipt.IssuedAtLocal,
            receipt.TimeZoneId,
            receipt.CashierName,
            receipt.RegisterName,
            receipt.TaxMode,
            originalSaleId,
            receipt.OriginalSaleNumber,
            receipt.VoidReason,
            receipt.RefundReason,
            [.. receipt.Lines.Select(line => new ReceiptLineResponse(
                line.LineNumber,
                line.Description,
                line.Quantity,
                (decimal)line.UnitPrice,
                line.TaxRate,
                (decimal)line.Discount,
                (decimal)line.LineTotal,
                line.IsPriceOverridden))],
            [.. receipt.TaxBreakdown.Select(tax => new ReceiptTaxLineResponse(
                tax.Rate,
                (decimal)tax.NetAmount,
                (decimal)tax.TaxAmount,
                (decimal)tax.GrossAmount))],
            [.. receipt.Tenders.Select(tender => new ReceiptTenderResponse(
                tender.Method,
                (decimal)tender.Amount,
                (decimal?)tender.ChangeGiven))],
            (decimal)receipt.Subtotal,
            (decimal)receipt.DiscountTotal,
            (decimal)receipt.TaxTotal,
            (decimal)receipt.RoundingAdjustment,
            (decimal)receipt.Total,
            (decimal)receipt.ChangeGiven);
    }
}
