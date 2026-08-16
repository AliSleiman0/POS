using Pos.Core.Entities;

namespace Pos.Data.Reporting;

/*
 * The shapes the report queries return.
 *
 * Amounts are `decimal`, not `Money`, and that is forced rather than chosen: these come back
 * through EF's `SqlQuery<T>`, which maps columns onto an unmapped type and therefore does not
 * run the `Money` value converter. `ReportQueries` converts at the boundary so nothing above it
 * handles a bare decimal — CLAUDE.md invariant 3 holds from there upwards.
 */

/// <summary>One tax rate's share of a report.</summary>
public sealed record TaxByRateRow(decimal Rate, decimal Net, decimal Tax);

/// <summary>One payment method's takings.</summary>
public sealed record TenderRow(string Method, decimal Amount, decimal ChangeGiven);

/// <summary>The headline figures, per sale type so refunds can be shown separately.</summary>
public sealed record SaleTotalsRow(
    string Type,
    int Count,
    decimal Subtotal,
    decimal Discount,
    decimal Tax,
    decimal Rounding,
    decimal Total,

    /// <summary>
    /// Cash left for the staff, summed separately and <b>never added to <see cref="Total"/></b>.
    /// </summary>
    /// <remarks>
    /// It is reported because it is in the drawer: expected cash sums tendered less change
    /// given, so the tips are already inside the figure a manager counts against. Without a line
    /// of its own, a shop that took €40 in tips reads as €40 over with nothing explaining it —
    /// the same failure as absorbing a cash-rounding adjustment instead of recording it.
    /// </remarks>
    decimal Tips);

/// <summary>A drop, payout or petty-cash movement, with who did it and why.</summary>
public sealed record CashMovementRow(
    Guid Id,
    string Type,
    decimal Amount,
    string Reason,
    string PerformedBy,
    DateTimeOffset OccurredAt);

/// <summary>A shift in scope, with the reconciliation it stored when it closed.</summary>
public sealed record ShiftRow(
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

/// <summary>
/// A void or a refund, listed individually.
/// </summary>
/// <remarks>
/// <b>With the actor and the reason</b>, which is the whole point of listing them — §6.3. A
/// count of voids is a number nobody can act on; "four voids, all by the same person, all
/// reason 'mistake'" is the thing an owner is looking at the report to find.
/// </remarks>
public sealed record ReversalRow(
    Guid SaleId,
    long SaleNumber,
    decimal Total,
    DateTimeOffset At,
    string Actor,
    string? Reason,
    long? OriginalSaleNumber);

/// <summary>One product's contribution to margin. Owner-only.</summary>
public sealed record MarginRow(
    Guid ProductId,
    string Description,
    decimal Quantity,
    decimal Revenue,
    decimal? Cost);

/// <summary>What one table took, and how long it was sat at.</summary>
/// <param name="TableName">The table's name <b>now</b>. See the remarks on the query.</param>
/// <param name="Covers">How many people, summed over the orders — the denominator of spend per head.</param>
/// <param name="Orders">How many sittings, which is what "turned twice" counts.</param>
/// <param name="Total">Taken, tips excluded. Revenue is revenue.</param>
/// <param name="Tips">Left on top, kept separate for the reason DATA-MODEL invariant 2 keeps it separate.</param>
/// <param name="MinutesSeated">Sum of each closed sitting's length. Null sittings — still open — contribute nothing.</param>
public sealed record TableTakingsRow(
    string TableName,
    int Covers,
    int Orders,
    decimal Total,
    decimal Tips,
    decimal? MinutesSeated);

/// <summary>What one member of staff took, and what guests left them.</summary>
/// <param name="ServerName">Whoever opened the order, which is who served the table.</param>
public sealed record ServerTakingsRow(
    string ServerName,
    int Covers,
    int Orders,
    decimal Total,
    decimal Tips);

/// <summary>Everything one report needs, read in one pass.</summary>
public sealed record ReportData(
    IReadOnlyList<SaleTotalsRow> Totals,
    IReadOnlyList<TaxByRateRow> TaxByRate,
    IReadOnlyList<TenderRow> Tenders,
    IReadOnlyList<CashMovementRow> CashMovements,
    IReadOnlyList<ShiftRow> Shifts,
    IReadOnlyList<ReversalRow> Voids,
    IReadOnlyList<ReversalRow> Refunds,

    /// <summary>
    /// Empty for a retail shop, and empty is the correct answer rather than a missing one.
    /// </summary>
    /// <remarks>
    /// The queries behind these run only in restaurant mode — a counter has no tables and no
    /// orders, so they would return nothing anyway, and skipping them keeps a retail Z-report
    /// exactly as many round trips as it was before this phase.
    /// </remarks>
    IReadOnlyList<TableTakingsRow> ByTable,
    IReadOnlyList<ServerTakingsRow> ByServer)
{
    /// <summary>The totals for ordinary sales, or zeroes if there were none.</summary>
    public SaleTotalsRow Sales => Totals.FirstOrDefault(t => t.Type == nameof(SaleType.Sale))
        ?? Empty(SaleType.Sale);

    /// <summary>The totals for refunds. Negative amounts.</summary>
    public SaleTotalsRow Returns => Totals.FirstOrDefault(t => t.Type == nameof(SaleType.Refund))
        ?? Empty(SaleType.Refund);

    /// <summary>
    /// Zeroes, for a scope in which nothing of this type was sold.
    /// </summary>
    /// <remarks>
    /// §6.3's exit criterion: a day with no sales renders as zeroes, not an error. That is not
    /// only politeness — a shop that opened and sold nothing still has to cash up, and a report
    /// that 404s is a report nobody can reconcile the float against.
    /// </remarks>
    private static SaleTotalsRow Empty(SaleType type) => new(type.ToString(), 0, 0m, 0m, 0m, 0m, 0m, 0m);
}
