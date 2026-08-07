using Microsoft.EntityFrameworkCore;
using Pos.Core.Tenancy;

namespace Pos.Data.Reporting;

/// <summary>
/// The aggregations behind the Z-report, the daily report and the margin report.
/// </summary>
/// <remarks>
/// <b>Raw SQL, for the reason <c>ShiftWriter</c> already documents:</b> EF cannot aggregate a
/// value-converted property, and every amount in this system is a <c>Money</c> — a struct over
/// <c>decimal</c> — so <c>SumAsync</c> over one does not translate. That is recorded in
/// DECISIONS.md as the known cost of typing entity amounts as <c>Money</c> and pinned by
/// <c>MoneyMappingTests.Summing_money_in_the_database_is_not_translatable</c>. The alternative,
/// loading a day's sale lines into memory to add them up, is not one.
/// <para>
/// The <c>tenant_id</c> predicate is written by hand for the same reason it is in
/// <c>ShiftWriter</c>: the global query filter composes over LINQ and not over this. It is not a
/// bypass of invariant 2 — the tenant comes from the validated token exactly as everywhere else,
/// and row-level security is underneath as the layer that holds when application code is wrong.
/// A test proves a second tenant's report is empty.
/// </para>
/// <para>
/// <b>Voided sales are excluded, never subtracted.</b> A void hands the cash straight back, so
/// the money never stayed in the drawer; netting it off would give the same total while making
/// the report claim takings that did not happen. Same rule as <c>ShiftArithmetic</c>'s, and the
/// two have to agree or the expected cash on a Z-report will not match the one stored on the
/// shift it is reporting.
/// </para>
/// </remarks>
public sealed class ReportQueries(AppDbContext db, ITenantContext tenant)
{
    /// <summary>Everything one report needs.</summary>
    public async Task<ReportData> ReadAsync(ReportScope scope, CancellationToken cancellationToken)
    {
        return new ReportData(
            await SaleTotalsAsync(scope, cancellationToken),
            await TaxByRateAsync(scope, cancellationToken),
            await TendersAsync(scope, cancellationToken),
            await CashMovementsAsync(scope, cancellationToken),
            await ShiftsAsync(scope, cancellationToken),
            await ReversalsAsync(scope, voided: true, cancellationToken),
            await ReversalsAsync(scope, voided: false, cancellationToken));
    }

    /// <summary>
    /// The headline figures, grouped by sale type.
    /// </summary>
    /// <remarks>
    /// The chain a reader can check by eye: <c>gross − discounts = net</c>, and
    /// <c>net + tax + rounding = total</c>. Both hold on the stored two-decimal values because
    /// the pricing engine derived <c>Subtotal</c> to make them hold (invariant 1), so summing
    /// the columns preserves it.
    /// </remarks>
    private Task<List<SaleTotalsRow>> SaleTotalsAsync(
        ReportScope scope,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<SaleTotalsRow>(
            $"""
             SELECT s.type                        AS type,
                    COUNT(*)::int                 AS count,
                    COALESCE(SUM(s.subtotal), 0)             AS subtotal,
                    COALESCE(SUM(s.discount_total), 0)       AS discount,
                    COALESCE(SUM(s.tax_total), 0)            AS tax,
                    COALESCE(SUM(s.rounding_adjustment), 0)  AS rounding,
                    COALESCE(SUM(s.total), 0)                AS total
             FROM sale s
             WHERE s.tenant_id = {tenant.TenantId}
               AND s.status <> 'Voided'
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR s.shift_id = {scope.ShiftId})
               AND s.completed_at >= {scope.StartUtc}
               AND s.completed_at <  {scope.EndUtc}
             GROUP BY s.type
             """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Tax by rate, from the snapshotted rate on each sale line.
    /// </summary>
    /// <remarks>
    /// <c>sale_line.tax_rate</c>, never <c>tax_class.rate</c>. A rate changes by law, and a VAT
    /// return filed against the current one would restate every past period the day it moved —
    /// the report version of invariant 5.
    /// <para>
    /// The net side is the line's subtotal less its discount, which is the taxable amount the
    /// tax was actually computed on. Reading <c>line_subtotal</c> alone would overstate the base
    /// on any discounted basket.
    /// </para>
    /// </remarks>
    private Task<List<TaxByRateRow>> TaxByRateAsync(
        ReportScope scope,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<TaxByRateRow>(
            $"""
             SELECT l.tax_rate AS rate,
                    COALESCE(SUM(l.line_subtotal - l.discount_amount), 0) AS net,
                    COALESCE(SUM(l.line_tax), 0)                          AS tax
             FROM sale_line l
             JOIN sale s ON s.id = l.sale_id AND s.tenant_id = l.tenant_id
             WHERE l.tenant_id = {tenant.TenantId}
               AND s.status <> 'Voided'
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR s.shift_id = {scope.ShiftId})
               AND s.completed_at >= {scope.StartUtc}
               AND s.completed_at <  {scope.EndUtc}
             GROUP BY l.tax_rate
             ORDER BY l.tax_rate
             """)
            .ToListAsync(cancellationToken);

    /// <summary>What came in, by method, net of the change handed back.</summary>
    private Task<List<TenderRow>> TendersAsync(
        ReportScope scope,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<TenderRow>(
            $"""
             SELECT t.method AS method,
                    COALESCE(SUM(t.amount), 0)                    AS amount,
                    COALESCE(SUM(COALESCE(t.change_given, 0)), 0) AS change_given
             FROM tender t
             JOIN sale s ON s.id = t.sale_id AND s.tenant_id = t.tenant_id
             WHERE t.tenant_id = {tenant.TenantId}
               AND s.status <> 'Voided'
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR s.shift_id = {scope.ShiftId})
               AND s.completed_at >= {scope.StartUtc}
               AND s.completed_at <  {scope.EndUtc}
             GROUP BY t.method
             ORDER BY t.method
             """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Drops, payouts and petty cash, listed individually.
    /// </summary>
    /// <remarks>
    /// Scoped on <c>occurred_at</c> for a date range rather than on the shift's own window: a
    /// drop to the safe is money leaving the drawer at a moment, and "what left the till today"
    /// is the question the cash section answers.
    /// </remarks>
    private Task<List<CashMovementRow>> CashMovementsAsync(
        ReportScope scope,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<CashMovementRow>(
            $"""
             SELECT m.id                                    AS id,
                    m.type                                  AS type,
                    m.amount                                AS amount,
                    m.reason                                AS reason,
                    COALESCE(u.display_name, 'Unknown')     AS performed_by,
                    m.occurred_at                           AS occurred_at
             FROM cash_movement m
             LEFT JOIN application_user u
                    ON u.id = m.performed_by AND u.tenant_id = m.tenant_id
             WHERE m.tenant_id = {tenant.TenantId}
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR m.shift_id = {scope.ShiftId})
               AND m.occurred_at >= {scope.StartUtc}
               AND m.occurred_at <  {scope.EndUtc}
             ORDER BY m.occurred_at
             """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// The shifts in scope, carrying the reconciliation they stored at close.
    /// </summary>
    /// <remarks>
    /// <b>The stored figures, never recomputed.</b> <c>Shift.ExpectedCash</c>'s own remarks say
    /// why: recomputing later would silently change a historical variance whenever anything
    /// underneath it changed, which is the same class of mistake as joining a report to the
    /// current product price. An <i>open</i> shift has none of them yet, and the caller computes
    /// a provisional figure and labels it as one.
    /// <para>
    /// <b>Scoped by overlap, not by the day the shift opened.</b> A drawer opened last night and
    /// still open is trading today, and its sales are in today's window — so scoping on
    /// <c>opened_at</c> alone produced a report with a day's cash takings, no opening float
    /// behind them, and no shift to say the drawer had not been counted. It read as fully
    /// reconciled. Found by the end-to-end test, because <c>pos_e2e</c> is never dropped and its
    /// drawer has been open since the first run.
    /// </para>
    /// <para>
    /// A shift spanning two days therefore appears on both, with its whole float and its whole
    /// variance. That is right rather than double-counting: neither figure is a daily total that
    /// anyone adds across days — they describe a drawer, and the drawer is the same one.
    /// </para>
    /// </remarks>
    private Task<List<ShiftRow>> ShiftsAsync(
        ReportScope scope,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<ShiftRow>(
            $"""
             SELECT sh.id                                  AS id,
                    COALESCE(r.name, 'Unknown')            AS register_name,
                    sh.status                              AS status,
                    COALESCE(o.display_name, 'Unknown')    AS opened_by,
                    sh.opened_at                           AS opened_at,
                    c.display_name                         AS closed_by,
                    sh.closed_at                           AS closed_at,
                    sh.opening_float                       AS opening_float,
                    sh.expected_cash                       AS expected_cash,
                    sh.counted_cash                        AS counted_cash,
                    sh.variance                            AS variance
             FROM shift sh
             LEFT JOIN register r      ON r.id = sh.register_id AND r.tenant_id = sh.tenant_id
             LEFT JOIN application_user o ON o.id = sh.opened_by   AND o.tenant_id = sh.tenant_id
             LEFT JOIN application_user c ON c.id = sh.closed_by   AND c.tenant_id = sh.tenant_id
             WHERE sh.tenant_id = {tenant.TenantId}
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR sh.id = {scope.ShiftId})
               AND sh.opened_at < {scope.EndUtc}
               AND (sh.closed_at IS NULL OR sh.closed_at >= {scope.StartUtc})
             ORDER BY sh.opened_at
             """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Voids, or refunds, with who did it and why.
    /// </summary>
    /// <remarks>
    /// One query for both because they differ only in which rows and which actor column, and
    /// two near-identical copies is how one of them quietly stops joining the actor.
    /// <para>
    /// Scoped on <c>completed_at</c> like everything else, so a void appears on the day the sale
    /// was <i>rung</i>. That is the day whose takings it changes, and the day whose report has
    /// to explain the gap in the sale numbers.
    /// </para>
    /// </remarks>
    private Task<List<ReversalRow>> ReversalsAsync(
        ReportScope scope,
        bool voided,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<ReversalRow>(
            $"""
             SELECT s.id AS sale_id,
                    s.sale_number AS sale_number,
                    s.total       AS total,
                    CASE WHEN {voided} THEN s.voided_at ELSE s.completed_at END AS at,
                    COALESCE(
                        CASE WHEN {voided} THEN v.display_name ELSE c.display_name END,
                        'Unknown')                                              AS actor,
                    CASE WHEN {voided} THEN s.void_reason ELSE s.refund_reason END AS reason,
                    o.sale_number AS original_sale_number
             FROM sale s
             LEFT JOIN application_user v ON v.id = s.voided_by AND v.tenant_id = s.tenant_id
             LEFT JOIN application_user c ON c.id = s.cashier_id AND c.tenant_id = s.tenant_id
             LEFT JOIN sale o          ON o.id = s.original_sale_id AND o.tenant_id = s.tenant_id
             WHERE s.tenant_id = {tenant.TenantId}
               AND (({voided} AND s.status = 'Voided')
                    OR (NOT {voided} AND s.status <> 'Voided' AND s.type = 'Refund'))
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR s.shift_id = {scope.ShiftId})
               AND s.completed_at >= {scope.StartUtc}
               AND s.completed_at <  {scope.EndUtc}
             ORDER BY s.sale_number
             """)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// What was sold, what it earned and what it cost. <b>Owner-only</b> at the endpoint.
    /// </summary>
    /// <remarks>
    /// Revenue is snapshotted; cost is <b>not</b>, and cannot be. <c>SaleLine</c> records no cost
    /// price, so this joins to <c>product.cost_price</c> as it stands today — which means a
    /// margin report restates itself when a supplier's price changes. That is a real limitation
    /// and it is stated in docs/API.md rather than hidden: fixing it properly means snapshotting
    /// cost onto the sale line, which is a schema change and a decision, not a query.
    /// <para>
    /// <c>Cost</c> is nullable because a product may have none — a service item, or one nobody
    /// entered a cost for. The caller shows those as unknown rather than as a 100% margin.
    /// </para>
    /// </remarks>
    public Task<List<MarginRow>> MarginsAsync(
        ReportScope scope,
        CancellationToken cancellationToken) =>
        db.Database.SqlQuery<MarginRow>(
            $"""
             SELECT l.product_id AS product_id,
                    MIN(l.description)                                     AS description,
                    COALESCE(SUM(l.quantity), 0)                           AS quantity,
                    COALESCE(SUM(l.line_subtotal - l.discount_amount), 0)   AS revenue,
                    CASE WHEN BOOL_OR(p.cost_price IS NULL) THEN NULL
                         ELSE SUM(l.quantity * p.cost_price) END           AS cost
             FROM sale_line l
             JOIN sale s     ON s.id = l.sale_id AND s.tenant_id = l.tenant_id
             LEFT JOIN product p ON p.id = l.product_id AND p.tenant_id = l.tenant_id
             WHERE l.tenant_id = {tenant.TenantId}
               AND s.status <> 'Voided'
               AND ({scope.ShiftId} = '00000000-0000-0000-0000-000000000000'::uuid
                    OR s.shift_id = {scope.ShiftId})
               AND s.completed_at >= {scope.StartUtc}
               AND s.completed_at <  {scope.EndUtc}
             GROUP BY l.product_id
             ORDER BY 4 DESC
             """)
            .ToListAsync(cancellationToken);
}
