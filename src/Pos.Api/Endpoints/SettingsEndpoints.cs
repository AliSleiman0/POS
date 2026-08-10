using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Core.Auditing;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>Everything a shop can see about how it is configured.</summary>
/// <remarks>
/// A superset of the <c>tenant</c> block on <c>GET /auth/me</c>, which stays as it is: the app
/// shell reads a currency code out of it on every page load, and making that depend on an
/// Owner-only screen's data would break the till for everybody else.
/// </remarks>
public sealed record SettingsResponse(
    string Name,
    string Slug,
    string CurrencyCode,
    string TimeZoneId,
    TaxMode TaxMode,
    bool TaxModeLocked,
    decimal CashRoundingIncrement,
    TimeSpan BusinessDayStartOffset,
    string? AddressLine,
    string? TaxNumber,
    string? ReceiptHeader,
    string? ReceiptFooter);

/// <summary>
/// The settings a shop may change about itself.
/// </summary>
/// <remarks>
/// <b>Currency, time zone and the business-day offset are deliberately absent.</b> Changing a
/// time zone or a day-start offset moves every trading-day boundary that has already been
/// reported on, so yesterday's Z-report stops matching yesterday. Changing a currency code
/// relabels every amount ever stored. Those are migrations with a decision behind them, not
/// fields on a settings form; see docs/API.md.
/// </remarks>
public sealed record UpdateSettingsRequest(
    string Name,
    TaxMode TaxMode,
    decimal CashRoundingIncrement,
    string? AddressLine,
    string? TaxNumber,
    string? ReceiptHeader,
    string? ReceiptFooter);

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettingsEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = builder.MapGroup("/api/v1/settings").WithTags("Settings");

        // CanSell, because the till needs the currency and the rounding rule to render a
        // total. Nothing here is commercially sensitive.
        settings.MapGet("/", GetAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("How this shop is configured");

        settings.MapPut("/", UpdateAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Change what a shop may change about itself");

        return builder;
    }

    private static async Task<Ok<SettingsResponse>> GetAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var shop = await ReadAsync(db, cancellationToken);
        var hasTraded = await db.Sales.AnyAsync(cancellationToken);

        return TypedResults.Ok(Project(shop, hasTraded));
    }

    /// <remarks>
    /// <b>The only write in this API with no query filter, no RLS policy and no interceptor
    /// check behind it.</b> <c>Tenant</c> is deliberately not tenant-owned — it is the list of
    /// tenants, and login has to resolve a row in it before any tenant is known — so all three
    /// layers that protect every other table are absent here. The single control is the
    /// <c>Where</c> in <see cref="ReadAsync"/>, matching how the receipt and cart paths already
    /// read this row. No id is accepted from the route or the body, and
    /// <c>SettingsTests.A_put_only_ever_touches_the_calling_tenants_row</c> is the test that
    /// means it.
    /// </remarks>
    private static async Task<Results<Ok<SettingsResponse>, ValidationProblem>> UpdateAsync(
        UpdateSettingsRequest request,
        AppDbContext db,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = Validate(request);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var shop = await ReadAsync(db, cancellationToken);
        var hasTraded = await db.Sales.AnyAsync(cancellationToken);

        if (request.TaxMode != shop.TaxMode && hasTraded)
        {
            throw new TaxModeLockedException();
        }

        // One entry per changed key, matching the phase table's "key, before, after". A single
        // entry carrying the whole object would make a reader diff two blobs to find out that
        // the footer changed — and would file a row every time somebody pressed Save with
        // nothing edited.
        Record(audit, shop.Id, "name", shop.Name, request.Name.Trim());
        Record(audit, shop.Id, "taxMode", shop.TaxMode.ToString(), request.TaxMode.ToString());
        Record(audit, shop.Id, "cashRoundingIncrement", shop.CashRoundingIncrement, request.CashRoundingIncrement);
        Record(audit, shop.Id, "addressLine", shop.AddressLine, Trimmed(request.AddressLine));
        Record(audit, shop.Id, "taxNumber", shop.TaxNumber, Trimmed(request.TaxNumber));
        Record(audit, shop.Id, "receiptHeader", shop.ReceiptHeader, Trimmed(request.ReceiptHeader));
        Record(audit, shop.Id, "receiptFooter", shop.ReceiptFooter, Trimmed(request.ReceiptFooter));

        shop.Name = request.Name.Trim();
        shop.TaxMode = request.TaxMode;
        shop.CashRoundingIncrement = request.CashRoundingIncrement;
        shop.AddressLine = Trimmed(request.AddressLine);
        shop.TaxNumber = Trimmed(request.TaxNumber);
        shop.ReceiptHeader = Trimmed(request.ReceiptHeader);
        shop.ReceiptFooter = Trimmed(request.ReceiptFooter);

        // One save, so the entries and the change they describe commit together.
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(Project(shop, hasTraded));
    }

    /// <summary>
    /// Stages a <c>SettingsChanged</c> entry for an amount, but only if the value moved.
    /// </summary>
    /// <remarks>
    /// Compared as a <see cref="decimal"/> and formatted afterwards, never the other way
    /// round. <c>numeric(19,4)</c> round-trips <c>0m</c> as <c>0.0000m</c>, and
    /// <c>decimal.ToString()</c> preserves that scale — so comparing the rendered strings
    /// reports "0" changing to "0.0000" and files an entry every time somebody presses Save
    /// with nothing edited. Which is exactly how a log stops being read.
    /// </remarks>
    private static void Record(IAuditLog audit, Guid tenantId, string key, decimal before, decimal after)
    {
        if (before == after)
        {
            return;
        }

        Record(
            audit,
            tenantId,
            key,
            before.ToString(CultureInfo.InvariantCulture),
            after.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Stages a <c>SettingsChanged</c> entry, but only if the value actually moved.</summary>
    private static void Record(IAuditLog audit, Guid tenantId, string key, string? before, string? after)
    {
        if (string.Equals(before, after, StringComparison.Ordinal))
        {
            return;
        }

        audit.Record(
            AuditAction.SettingsChanged,
            nameof(Tenant),
            tenantId,
            before: new Dictionary<string, string?> { [key] = before },
            after: new Dictionary<string, string?> { [key] = after });
    }

    /// <remarks>
    /// Tracked, not <c>AsNoTracking</c> — the PUT edits this instance. Filtered by
    /// <c>CurrentTenantId</c> by hand because <c>Tenant</c> carries no query filter; see the
    /// remarks on <see cref="UpdateAsync"/>.
    /// </remarks>
    private static Task<Tenant> ReadAsync(AppDbContext db, CancellationToken cancellationToken) =>
        db.Tenants.FirstAsync(t => t.Id == db.CurrentTenantId, cancellationToken);

    private static SettingsResponse Project(Tenant shop, bool hasTraded) =>
        new(
            shop.Name,
            shop.Slug,
            shop.CurrencyCode,
            shop.TimeZoneId,
            shop.TaxMode,

            // Sent rather than left for the client to work out, so a screen can render the
            // control as read-only with a reason instead of offering one that 409s.
            TaxModeLocked: hasTraded,
            shop.CashRoundingIncrement,
            shop.BusinessDayStartOffset,
            shop.AddressLine,
            shop.TaxNumber,
            shop.ReceiptHeader,
            shop.ReceiptFooter);

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static Dictionary<string, string[]> Validate(UpdateSettingsRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200)
        {
            errors["name"] = ["A shop name of 1 to 200 characters is required."];
        }

        if (!Enum.IsDefined(request.TaxMode))
        {
            errors["taxMode"] = [$"One of {string.Join(", ", Enum.GetNames<TaxMode>())} is required."];
        }

        // Non-negative and storable, and additionally not more than a whole currency unit: an
        // increment of 5 would round every cash total to the nearest fiver, which is a typo
        // for 0.05 rather than a rounding rule anybody has.
        if (request.CashRoundingIncrement < 0m
            || request.CashRoundingIncrement > 1m
            || !CatalogRules.IsStorableAmount(request.CashRoundingIncrement))
        {
            errors["cashRoundingIncrement"] =
                ["A rounding increment between 0 and 1, with at most 4 decimal places, is required."];
        }

        Bounded(errors, "addressLine", request.AddressLine, Tenant.AddressLineMaxLength);
        Bounded(errors, "taxNumber", request.TaxNumber, Tenant.TaxNumberMaxLength);
        Bounded(errors, "receiptHeader", request.ReceiptHeader, Tenant.ReceiptTextMaxLength);
        Bounded(errors, "receiptFooter", request.ReceiptFooter, Tenant.ReceiptTextMaxLength);

        return errors;
    }

    private static void Bounded(
        Dictionary<string, string[]> errors,
        string field,
        string? value,
        int maxLength)
    {
        if (value is not null && value.Length > maxLength)
        {
            errors[field] = [$"At most {maxLength.ToString(CultureInfo.InvariantCulture)} characters."];
        }
    }
}
