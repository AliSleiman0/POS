using Pos.Core.Entities;
using Pos.Core.Monetary;

namespace Pos.Core.Receipts;

/// <summary>
/// Turns a stored sale into the payload a receipt is rendered from. Pure: no database, no
/// clock, no time-zone lookup.
/// </summary>
/// <remarks>
/// <b>The time zone arrives as a parameter, not as an id to look up.</b>
/// <c>TimeZoneInfo.FindSystemTimeZoneById</c> reads the operating system's zone database, which
/// is file access, and invariant 1 keeps that out of Core. The API resolves
/// <c>Tenant.TimeZoneId</c> and hands the zone in — which also means a test can pin a receipt
/// to Europe/Dublin without depending on what the machine running it is set to.
/// <para>
/// The instant is a parameter for the same reason: <c>issuedAt</c> comes from the caller's
/// <c>TimeProvider</c>, so a receipt rendered in a test is deterministic.
/// </para>
/// </remarks>
public static class ReceiptBuilder
{
    /// <summary>Builds the receipt for <paramref name="source"/>.</summary>
    /// <param name="source">The sale and everything printed alongside it.</param>
    /// <param name="zone">The tenant's zone, resolved from <c>Tenant.TimeZoneId</c> by the caller.</param>
    /// <param name="issuedAt">When this copy is being produced, in UTC.</param>
    public static Receipt Build(ReceiptSource source, TimeZoneInfo zone, DateTimeOffset issuedAt)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(zone);

        var sale = source.Sale;

        var lines = source.Lines
            .OrderBy(line => line.LineNumber)
            .Select(line => new ReceiptLine(
                line.LineNumber,
                line.Description,
                line.Quantity,
                line.UnitPrice,
                line.TaxRate,
                line.DiscountAmount,
                line.LineTotal,
                line.IsPriceOverridden))
            .ToArray();

        var tenders = source.Tenders
            .Select(tender => new ReceiptTender(tender.Method, tender.Amount, tender.ChangeGiven))
            .ToArray();

        return new Receipt(
            Kind: KindOf(sale),
            Shop: new ReceiptShop(
                source.Shop.Name,
                Trimmed(source.Shop.AddressLine),
                Trimmed(source.Shop.TaxNumber),
                Trimmed(source.Shop.ReceiptHeader),
                Trimmed(source.Shop.ReceiptFooter),
                source.Shop.CurrencyCode),
            SaleNumber: sale.SaleNumber,
            CompletedAtLocal: TimeZoneInfo.ConvertTime(sale.CompletedAt, zone),
            IssuedAtLocal: TimeZoneInfo.ConvertTime(issuedAt, zone),
            TimeZoneId: source.Shop.TimeZoneId,
            CashierName: source.CashierName,
            RegisterName: source.RegisterName,
            TaxMode: sale.TaxMode,
            OriginalSaleNumber: source.OriginalSaleNumber,
            VoidReason: sale.VoidReason,
            RefundReason: sale.RefundReason,
            Lines: lines,
            TaxBreakdown: BreakDownTax(source.Lines, sale),
            Tenders: tenders,
            Subtotal: sale.Subtotal,
            DiscountTotal: sale.DiscountTotal,
            TaxTotal: sale.TaxTotal,
            RoundingAdjustment: sale.RoundingAdjustment,
            Total: sale.Total,

            // Summed rather than taken from the first tender: change is recorded against the
            // tender that produced it, and a split payment can put it anywhere in the list.
            ChangeGiven: Money.Sum(source.Tenders.Select(t => t.ChangeGiven ?? Money.Zero)));
    }

    /// <summary>
    /// Groups the lines by tax rate, so that the parts sum to the sale's own totals
    /// <b>exactly</b>.
    /// </summary>
    /// <remarks>
    /// This is the part of a receipt that is easy to get subtly wrong. Line amounts are stored
    /// at <c>numeric(19,4)</c> and the sale header is rounded to the payable scale once, by the
    /// pricing engine. Rounding each rate's group independently and printing the results gives
    /// a breakdown that misses <c>TaxTotal</c> by a cent on some baskets and not others — and a
    /// receipt whose tax lines do not add up to its tax total is the one a tax authority asks
    /// about.
    /// <para>
    /// So the residue is <i>defined</i> as what is left over and placed on the largest group by
    /// <see cref="Reconciliation.RoundToSum"/> — shared with the Z-report, which has the same
    /// problem one level up and had it for the same reason.
    /// </para>
    /// </remarks>
    private static IReadOnlyList<ReceiptTaxLine> BreakDownTax(IReadOnlyList<SaleLine> lines, Sale sale)
    {
        // Ascending by rate, so the order is stable across two renders of one sale and reads
        // the way a person expects — zero-rated first. It also fixes which group a tie in the
        // residue step lands on.
        var groups = lines
            .GroupBy(line => line.TaxRate)
            .OrderBy(group => group.Key)
            .Select(group => new
            {
                Rate = group.Key,

                // The taxable amount at this rate: what is left of the line after both
                // discounts, net of tax. Accumulated at full precision, like the engine does.
                Net = Money.Sum(group.Select(l => l.LineSubtotal - l.DiscountAmount)),
                Tax = Money.Sum(group.Select(l => l.LineTax)),
            })
            .ToArray();

        if (groups.Length == 0)
        {
            // No lines, so nothing to break down. Unreachable through the API — POST /sales
            // refuses an empty cart — and returning empty rather than throwing keeps a receipt
            // printable for a row that somehow got in by other means.
            return [];
        }

        var taxes = Reconciliation.RoundToSum([.. groups.Select(g => g.Tax)], sale.TaxTotal);

        // The net side has to reconcile too, and against a derived figure. The engine defines
        // Subtotal so that Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment
        // holds on the stored values, which makes (Subtotal − DiscountTotal) the taxable amount
        // the whole sale was taxed on. The groups add up to that, or the receipt shows a
        // taxable base that disagrees with its own total.
        var nets = Reconciliation.RoundToSum(
            [.. groups.Select(g => g.Net)], sale.Subtotal - sale.DiscountTotal);

        return [.. groups.Select((group, index) => new ReceiptTaxLine(
            group.Rate,
            nets[index],
            taxes[index],
            nets[index] + taxes[index]))];
    }

    /// <summary>
    /// What this receipt is for.
    /// </summary>
    /// <remarks>
    /// Status is checked before type, so a voided refund prints as a void. That is the right
    /// way round: "this was reversed" is the fact the person reading it needs first, and it is
    /// the one that must not be missable.
    /// </remarks>
    private static ReceiptKind KindOf(Sale sale) => sale switch
    {
        { Status: SaleStatus.Voided } => ReceiptKind.VoidedSale,
        { Type: SaleType.Refund } => ReceiptKind.Refund,
        _ => ReceiptKind.Sale,
    };

    /// <summary>
    /// Blank-as-absent, so a tenant whose footer is a stray space does not print an empty line.
    /// </summary>
    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
