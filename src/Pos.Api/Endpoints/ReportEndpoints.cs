using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Reporting;
using Pos.Data;
using Pos.Data.Reporting;

namespace Pos.Api.Endpoints;

/// <summary>One product's margin over the window. Owner-only.</summary>
/// <param name="Cost">
/// Null when the product has no cost price. Shown as unknown rather than as a 100% margin,
/// which is what a zero would read as.
/// </param>
public sealed record MarginLineResponse(
    Guid ProductId,
    string Description,
    decimal Quantity,
    decimal Revenue,
    decimal? Cost,
    decimal? Margin);

/// <summary>Margins over a window, Owner-only.</summary>
public sealed record MarginReportResponse(
    ReportScopeResponse Scope,
    string CurrencyCode,
    decimal Revenue,
    decimal? Cost,
    decimal? Margin,
    IReadOnlyList<MarginLineResponse> Lines);

public static class ReportEndpoints
{
    public static IEndpointRouteBuilder MapReportEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var reports = builder.MapGroup("/api/v1/reports").WithTags("Reports");

        // CanCloseShift, not CanSell: this is the reconciliation view, and a cashier who could
        // read the day's expected cash could also work out what a drawer would tolerate.
        reports.MapGet("/daily", DailyAsync)
            .RequireAuthorization(Policies.CanCloseShift)
            .WithSummary("The trading day's report");

        reports.MapGet("/margins", MarginsAsync)
            .RequireAuthorization(Policies.CanViewMargins)
            .WithSummary("Margin by product over a window. Owner only");

        return builder;
    }

    /// <summary>
    /// Everything that happened on one trading day.
    /// </summary>
    /// <remarks>
    /// <b><c>?date=</c> is the tenant's trading day, not a UTC one.</b> Resolved through
    /// <c>Tenant.TimeZoneId</c> and <c>BusinessDayStartOffset</c>, so a shop with a 04:00 day
    /// start has its 02:00 close land on the previous day — which is what the staff who worked
    /// it would say, and getting it wrong makes every daily figure disagree with what people
    /// remember selling.
    /// <para>
    /// A day with no sales is a 200 of zeroes, never a 404. A shop that opened and sold nothing
    /// still has to cash up against its float, and an error would leave it nothing to do that
    /// with.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<ReportResponse>, ValidationProblem>> DailyAsync(
        string? date,
        AppDbContext db,
        ReportQueries reports,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var shop = await ShopAsync(db, cancellationToken);
        var zone = TenantTimeZone.Resolve(shop.TimeZoneId);

        if (!TryReadDate(date, zone, shop.BusinessDayStartOffset, timeProvider, out var day))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["date"] = ["A date in the form 2026-08-07 is required, or omit it for today."],
            });
        }

        var (startUtc, endUtc) = BusinessDay.Range(day, zone, shop.BusinessDayStartOffset);
        var scope = ReportScope.ForRange(startUtc, endUtc);

        var data = await reports.ReadAsync(scope, cancellationToken);

        return TypedResults.Ok(ReportResponse.From(
            new ReportScopeResponse("Day", null, day, startUtc, endUtc, shop.TimeZoneId),
            shop.CurrencyCode,
            data,
            LiveExpectedCash(data)));
    }

    /// <summary>
    /// Margin by product over a window of trading days. <b>Owner only.</b>
    /// </summary>
    /// <remarks>
    /// Revenue comes from the sale's snapshots; <b>cost does not, and cannot yet</b>.
    /// <c>SaleLine</c> records no cost price, so this reads <c>Product.CostPrice</c> as it
    /// stands today — a supplier price change restates every past period. That is a real
    /// limitation, recorded in docs/API.md rather than hidden, and fixing it means snapshotting
    /// cost onto the line: a schema change and a decision, not a query.
    /// </remarks>
    private static async Task<Results<Ok<MarginReportResponse>, ValidationProblem>> MarginsAsync(
        string? from,
        string? to,
        AppDbContext db,
        ReportQueries reports,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var shop = await ShopAsync(db, cancellationToken);
        var zone = TenantTimeZone.Resolve(shop.TimeZoneId);
        var offset = shop.BusinessDayStartOffset;

        var errors = new Dictionary<string, string[]>();

        if (!TryReadDate(from, zone, offset, timeProvider, out var firstDay))
        {
            errors["from"] = ["A date in the form 2026-08-07 is required, or omit it for today."];
        }

        if (!TryReadDate(to, zone, offset, timeProvider, out var lastDay))
        {
            errors["to"] = ["A date in the form 2026-08-07 is required, or omit it for today."];
        }

        if (errors.Count == 0 && lastDay < firstDay)
        {
            errors["to"] = ["The end of the window cannot be before its start."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Half-open across the whole window: the first day's start to the day after the last
        // day's start, so `from=to=today` is one full trading day rather than an empty range.
        var startUtc = BusinessDay.Range(firstDay, zone, offset).StartUtc;
        var endUtc = BusinessDay.Range(lastDay, zone, offset).EndUtc;

        var rows = await reports.MarginsAsync(ReportScope.ForRange(startUtc, endUtc), cancellationToken);

        var revenue = rows.Sum(r => r.Revenue);

        // Null, not zero, the moment one product's cost is unknown: a total that silently
        // treated it as free would overstate the margin, and overstating a margin is the
        // direction that gets a shop into trouble.
        var cost = rows.Any(r => r.Cost is null) ? (decimal?)null : rows.Sum(r => r.Cost!.Value);

        return TypedResults.Ok(new MarginReportResponse(
            new ReportScopeResponse("Day", null, firstDay, startUtc, endUtc, shop.TimeZoneId),
            shop.CurrencyCode,
            revenue,
            cost,
            cost is null ? null : revenue - cost,
            [.. rows.Select(r => new MarginLineResponse(
                r.ProductId,
                r.Description,
                r.Quantity,
                r.Revenue,
                r.Cost,
                r.Cost is null ? null : r.Revenue - r.Cost))]));
    }

    /// <summary>
    /// What is expected in the drawer of a shift that is still open.
    /// </summary>
    /// <remarks>
    /// Uses <c>ShiftArithmetic</c> — the same function the close itself calls — so a provisional
    /// figure and the one that gets stored minutes later cannot be computed two different ways.
    /// Only the cash that is genuinely in the drawer counts: <c>Cash</c> tenders net of change,
    /// plus the float, plus the signed movements.
    /// </remarks>
    private static Money LiveExpectedCash(ReportData data)
    {
        var open = data.Shifts
            .Where(s => s.Status == nameof(ShiftStatus.Open))
            .ToArray();

        if (open.Length == 0)
        {
            return Money.Zero;
        }

        var cash = data.Tenders.FirstOrDefault(t => t.Method == nameof(TenderMethod.Cash));

        return Core.Shifts.ShiftArithmetic.ExpectedCash(
            (Money)open.Sum(s => s.OpeningFloat),
            (Money)(cash is null ? 0m : cash.Amount - cash.ChangeGiven),
            (Money)data.CashMovements.Sum(m => m.Amount));
    }

    /// <summary>The tenant's own settings, which every report needs before it can start.</summary>
    internal static Task<TenantReportSettings> ShopAsync(
        AppDbContext db,
        CancellationToken cancellationToken) =>
        db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == db.CurrentTenantId)
            .Select(t => new TenantReportSettings(
                t.CurrencyCode, t.TimeZoneId, t.BusinessDayStartOffset))
            .FirstAsync(cancellationToken);

    /// <summary>
    /// Reads a <c>?date=</c>, defaulting to the tenant's <b>current trading day</b>.
    /// </summary>
    /// <remarks>
    /// Not "today" as the server's calendar sees it. At 01:00 in a shop with a 04:00 day start,
    /// the report somebody wants is yesterday's — they are still working it.
    /// </remarks>
    internal static bool TryReadDate(
        string? value,
        TimeZoneInfo zone,
        TimeSpan dayStart,
        TimeProvider timeProvider,
        out DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            date = BusinessDay.Of(timeProvider.GetUtcNow(), zone, dayStart);
            return true;
        }

        return DateOnly.TryParseExact(
            value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }
}

/// <summary>The three tenant settings every report reads.</summary>
internal sealed record TenantReportSettings(
    string CurrencyCode,
    string TimeZoneId,
    TimeSpan BusinessDayStartOffset);
