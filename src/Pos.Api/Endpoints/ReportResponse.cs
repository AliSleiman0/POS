using Pos.Core.Monetary;
using Pos.Core.Shifts;
using Pos.Data.Reporting;

namespace Pos.Api.Endpoints;

/// <summary>What a report covers, so a reader can tell one printout from another.</summary>
/// <param name="Kind"><c>Shift</c> or <c>Day</c>.</param>
/// <param name="Date">The trading day, for a daily report. Null for a shift.</param>
/// <param name="FromUtc">The half-open window's start. Instants outside it are not counted.</param>
public sealed record ReportScopeResponse(
    string Kind,
    Guid? ShiftId,
    DateOnly? Date,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    string TimeZoneId);

/// <summary>
/// The headline chain, in the order a person checks it.
/// </summary>
/// <remarks>
/// <c>gross − discounts = net</c> and <c>net + tax + rounding = total</c>, both exactly, on the
/// stored two-decimal values. <c>rounding</c> is here rather than folded away because §6.3 is
/// explicit about it: an unexplained rounding total means the rule is being applied
/// inconsistently somewhere, and absorbing it would remove the only evidence of that.
/// </remarks>
public sealed record ReportSalesResponse(
    int TransactionCount,
    decimal Gross,
    decimal Discounts,
    decimal Net,
    decimal Tax,
    decimal Rounding,
    decimal Total,
    decimal RefundTotal,
    int RefundCount,
    decimal AverageBasket,

    /// <summary>
    /// Cash left for the staff, on its own line and <b>not inside <see cref="Total"/></b>.
    /// </summary>
    /// <remarks>
    /// The reconciliation has to be <i>legible</i>, which is the actual requirement rather than
    /// merely balancing. Expected cash already contains the tips — it sums tendered less change
    /// given, and a tip is over-tender that stayed in the drawer — so a shop that took €40 in
    /// tips and did not see this line would read as €40 over with nothing to explain it.
    /// </remarks>
    decimal Tips = 0m);

public sealed record ReportTaxLineResponse(decimal Rate, decimal Net, decimal Tax);

public sealed record ReportTenderResponse(
    string Method,
    decimal Amount,
    decimal ChangeGiven,
    decimal Net);

public sealed record ReportCashMovementResponse(
    Guid Id,
    string Type,
    decimal Amount,
    string Reason,
    string PerformedBy,
    DateTimeOffset OccurredAt);

/// <summary>
/// The drawer.
/// </summary>
/// <remarks>
/// <b><see cref="IsProvisional"/> is the field that keeps this honest.</b> A closed shift's
/// expected cash and variance are read from the row the close stored, never recomputed —
/// recomputing would silently rewrite a historical variance whenever anything underneath
/// changed. A shift still open has none, so the figure here is computed live and flagged, and
/// <see cref="Counted"/> and <see cref="Variance"/> are null because nobody has counted the
/// drawer yet. Collapsing the two into one number would let a provisional figure be read as a
/// reconciled one.
/// </remarks>
public sealed record ReportCashResponse(
    decimal OpeningFloat,
    decimal CashSales,
    decimal CashRefunds,
    decimal CashMovements,
    decimal Expected,
    decimal? Counted,
    decimal? Variance,
    bool IsProvisional,
    IReadOnlyList<ReportCashMovementResponse> Movements);

public sealed record ReportShiftResponse(
    Guid Id,
    string RegisterName,
    string Status,
    string OpenedBy,
    DateTimeOffset OpenedAt,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    decimal OpeningFloat,
    decimal? ExpectedCash,
    decimal? CountedCash,
    decimal? Variance);

/// <summary>A void or a refund, with the actor and the reason §6.3 requires.</summary>
public sealed record ReportReversalResponse(
    Guid SaleId,
    long SaleNumber,
    decimal Total,
    DateTimeOffset At,
    string Actor,
    string? Reason,
    long? OriginalSaleNumber);

/// <summary>What one table took over the window.</summary>
/// <param name="AverageSpendPerCover">
/// Takings divided by covers — the number a restaurant owner manages by. Null where nobody keyed
/// a cover count, because a guessed denominator is worse than no average at all.
/// </param>
/// <param name="AverageMinutesPerSitting">
/// How long a sitting lasts, over the sittings that have ended. Null while the table is still
/// occupied, which is the honest answer rather than a turn time that grows as the evening does.
/// </param>
public sealed record ReportTableResponse(
    string TableName,
    int Covers,
    int Orders,
    decimal Total,
    decimal Tips,
    decimal? AverageSpendPerCover,
    decimal? AverageMinutesPerSitting);

/// <summary>What one member of staff took, and what guests left them.</summary>
public sealed record ReportServerResponse(
    string ServerName,
    int Covers,
    int Orders,
    decimal Total,
    decimal Tips,
    decimal? AverageSpendPerCover);

/// <summary>
/// A Z-report or a daily report. <b>The same shape for both</b>, deliberately.
/// </summary>
/// <remarks>
/// A shift's report and the day's report that contains it have to add up to the same money, and
/// two shapes with two sets of queries behind them is how that stops being true. One
/// <see cref="ReportScope"/> feeds one set of aggregations; only the window differs.
/// </remarks>
public sealed record ReportResponse(
    ReportScopeResponse Scope,
    string CurrencyCode,
    ReportSalesResponse Sales,
    IReadOnlyList<ReportTaxLineResponse> TaxByRate,
    IReadOnlyList<ReportTenderResponse> Tenders,
    ReportCashResponse Cash,
    IReadOnlyList<ReportShiftResponse> Shifts,
    IReadOnlyList<ReportReversalResponse> Voids,
    IReadOnlyList<ReportReversalResponse> Refunds,

    /// <summary>
    /// Empty at a counter, and empty rather than absent.
    /// </summary>
    /// <remarks>
    /// A retail shop has no tables, so these are always empty for one and the client renders
    /// nothing. Absent would have meant a nullable section every reader had to branch on for a
    /// distinction that is already visible in the data.
    /// <para>
    /// <b>Every figure comes off the <c>sale</c> row</b>, joined through <c>OrderBill.SaleId</c>
    /// — never from the current catalog. Invariant 5: pricing an old table from today's menu
    /// would retroactively rewrite what the shop took.
    /// </para>
    /// </remarks>
    IReadOnlyList<ReportTableResponse> ByTable,
    IReadOnlyList<ReportServerResponse> ByServer)
{
    /// <summary>Assembles the report from what the queries returned.</summary>
    /// <param name="liveExpectedCash">
    /// What is expected in the drawer of any shift still open, computed now. Ignored when every
    /// shift in scope has closed and stored its own figure.
    /// </param>
    public static ReportResponse From(
        ReportScopeResponse scope,
        string currencyCode,
        ReportData data,
        Money liveExpectedCash)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(data);

        var sales = data.Sales;
        var returns = data.Returns;

        // Both types together: a refunded day took less money, and the headline figures are
        // what the shop actually did. Refunds are also broken out on their own.
        var net = sales.Subtotal - sales.Discount + returns.Subtotal - returns.Discount;
        var tax = sales.Tax + returns.Tax;
        var total = sales.Total + returns.Total;
        var count = sales.Count + returns.Count;

        /*
         * The breakdown has to add up to the headline, and it does not on its own.
         *
         * `sale.tax_total` was rounded to the payable scale once per sale by the pricing
         * engine; `sale_line.line_tax` is stored at four places. Summing three sales' headers
         * and summing their lines therefore differ by the per-sale residue — 2.9000 against
         * 2.8980 on the basket that first caught this. A report whose VAT rows do not add up to
         * its own VAT total is the one an accountant queries, so the residue is placed
         * deliberately, exactly as a receipt's is.
         */
        var taxParts = Reconciliation.RoundToSum(
            [.. data.TaxByRate.Select(t => (Money)t.Tax)], (Money)tax);

        var netParts = Reconciliation.RoundToSum(
            [.. data.TaxByRate.Select(t => (Money)t.Net)], (Money)net);

        var openingFloat = data.Shifts.Sum(s => s.OpeningFloat);
        var movements = data.CashMovements.Sum(m => m.Amount);

        var cash = data.Tenders.FirstOrDefault(t => t.Method == nameof(Core.Entities.TenderMethod.Cash));
        var cashNet = cash is null ? 0m : cash.Amount - cash.ChangeGiven;

        // Split for the reader, from the sale-type totals rather than from a second query: a
        // refund's cash tender is negative, so what is left after taking the returns out is
        // what came in over the counter.
        var cashRefunds = returns.Total < 0m ? returns.Total : 0m;

        var open = data.Shifts.Where(s => s.Status == nameof(Core.Entities.ShiftStatus.Open)).ToArray();
        var closed = data.Shifts.Where(s => s.Status != nameof(Core.Entities.ShiftStatus.Open)).ToArray();

        var isProvisional = open.Length > 0;

        var expected = closed.Sum(s => s.ExpectedCash ?? 0m)
            + (isProvisional ? (decimal)liveExpectedCash : 0m);

        return new ReportResponse(
            scope,
            currencyCode,
            new ReportSalesResponse(
                count,
                sales.Subtotal + returns.Subtotal,
                sales.Discount + returns.Discount,
                net,
                tax,
                sales.Rounding + returns.Rounding,
                total,
                returns.Total,
                returns.Count,
                ZReportArithmetic.AverageBasket((Money)total, count).ToDecimal(),

                // Sales only. A refund carries no tip — a shop does not take one for handing
                // money back — so summing both would be adding a column that is always zero
                // and inviting somebody to wonder whether it should be.
                sales.Tips),
            [.. data.TaxByRate.Select((t, index) => new ReportTaxLineResponse(
                t.Rate, netParts[index].ToDecimal(), taxParts[index].ToDecimal()))],
            [.. data.Tenders.Select(t => new ReportTenderResponse(
                t.Method, t.Amount, t.ChangeGiven, t.Amount - t.ChangeGiven))],
            new ReportCashResponse(
                openingFloat,
                cashNet - cashRefunds,
                cashRefunds,
                movements,
                expected,

                // Only a closed shift has been counted. Summed rather than shown per shift so a
                // day covering three drawers has one variance an owner can act on; the per-shift
                // figures are below.
                closed.Length == 0 ? null : closed.Sum(s => s.CountedCash ?? 0m),
                closed.Length == 0 ? null : closed.Sum(s => s.Variance ?? 0m),
                isProvisional,
                [.. data.CashMovements.Select(m => new ReportCashMovementResponse(
                    m.Id, m.Type, m.Amount, m.Reason, m.PerformedBy, m.OccurredAt))]),
            [.. data.Shifts.Select(s => new ReportShiftResponse(
                s.Id,
                s.RegisterName,
                s.Status,
                s.OpenedBy,
                s.OpenedAt,
                s.ClosedBy,
                s.ClosedAt,
                s.OpeningFloat,
                s.ExpectedCash,
                s.CountedCash,
                s.Variance))],
            [.. data.Voids.Select(Reversal)],
            [.. data.Refunds.Select(Reversal)],
            [.. data.ByTable.Select(t => new ReportTableResponse(
                t.TableName,
                t.Covers,
                t.Orders,
                t.Total,
                t.Tips,
                PerCover(t.Total, t.Covers),
                PerSitting(t.MinutesSeated, t.Orders)))],
            [.. data.ByServer.Select(s => new ReportServerResponse(
                s.ServerName,
                s.Covers,
                s.Orders,
                s.Total,
                s.Tips,
                PerCover(s.Total, s.Covers)))]);
    }

    private static ReportReversalResponse Reversal(ReversalRow row) =>
        new(row.SaleId, row.SaleNumber, row.Total, row.At, row.Actor, row.Reason, row.OriginalSaleNumber);

    /// <summary>
    /// Spend per head, or null where nobody keyed a cover count.
    /// </summary>
    /// <remarks>
    /// <b>Null rather than the takings themselves.</b> Dividing by a missing denominator and
    /// calling the result an average would put a plausible, wrong number on a report an owner
    /// makes decisions with — and a takeaway legitimately has no covers at all. Money rounding
    /// through <see cref="Money"/>, like every other amount here.
    /// </remarks>
    private static decimal? PerCover(decimal total, int covers) =>
        covers <= 0 ? null : ((Money)total / covers).ToDecimal();

    /// <summary>
    /// How long a sitting lasted, over the sittings that have ended.
    /// </summary>
    /// <remarks>
    /// Not a <see cref="Money"/>: minutes are a duration, and rounding them to four decimal
    /// places would be borrowing a rule that exists for cash. Rounded to a whole minute, which
    /// is the precision anybody reads a turn time at.
    /// </remarks>
    private static decimal? PerSitting(decimal? minutes, int sittings) =>
        minutes is not { } total || sittings <= 0
            ? null
            : Math.Round(total / sittings, 0, MidpointRounding.AwayFromZero);
}
