using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>A product, as the offline mirror stores it.</summary>
/// <remarks>
/// <b>No <c>costPrice</c>, and not because of a projection trick.</b> The feed is
/// <c>CanSell</c>, so a Cashier's till receives it, and anything that reaches a browser is
/// readable (invariant 7). Margin is a reporting question and reports are online-only, so the
/// column is simply not part of what an offline register needs — which is a better answer than
/// two projections, because there is then no shape of this response that carries it.
/// </remarks>
public sealed record SyncProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    Guid? CategoryId,
    Guid TaxClassId,
    decimal UnitPrice,
    Unit Unit,
    bool IsActive,
    bool TrackStock,
    DateTimeOffset ChangedAt);

/// <summary>A code that scans to a product, as the mirror stores it.</summary>
public sealed record SyncBarcodeResponse(
    Guid Id,
    Guid ProductId,
    string Code,
    bool IsPrimary,
    DateTimeOffset ChangedAt);

/// <summary>A tax class. The register needs the rate to price a line offline.</summary>
public sealed record SyncTaxClassResponse(
    Guid Id,
    string Name,
    decimal Rate,
    bool IsDefault,
    DateTimeOffset ChangedAt);

/// <summary>A category, for the product grid's grouping.</summary>
public sealed record SyncCategoryResponse(
    Guid Id,
    string Name,
    Guid? ParentCategoryId,
    int SortOrder,
    bool IsActive,
    DateTimeOffset ChangedAt);

/// <summary>
/// The tenant settings an offline till needs to price and print.
/// </summary>
/// <remarks>
/// A subset of <c>GET /settings</c>, which is <c>CanSell</c> too but returns fields an offline
/// register has no use for. Sent whole on every sync rather than incrementally: it is one small
/// object, and a mirror that had a stale <c>taxMode</c> would price every cart wrongly.
/// </remarks>
public sealed record SyncSettingsResponse(
    string CurrencyCode,
    string TimeZoneId,
    TaxMode TaxMode,
    decimal CashRoundingIncrement,
    TimeSpan BusinessDayStartOffset,
    string? AddressLine,
    string? TaxNumber,
    string? ReceiptHeader,
    string? ReceiptFooter);

/// <summary>
/// Everything that changed since the caller's watermark, plus the new watermark.
/// </summary>
/// <remarks>
/// One response rather than four endpoints, deliberately. A till stitching a product feed to a
/// barcode feed to a tax-class feed has to reconcile three watermarks itself, and the window
/// between them is where a product arrives whose tax class has not — which prices a cart at
/// zero rather than failing. One call, one watermark, one thing to get right.
/// </remarks>
/// <param name="Products">Products created or changed since <c>?since=</c>.</param>
/// <param name="Barcodes">Codes created or changed, excluding withdrawn ones.</param>
/// <param name="RemovedBarcodeIds">
/// Codes withdrawn since <c>?since=</c>. <b>The half a naive feed leaves out</b>: without it a
/// till goes on scanning a code the shop retired, at a price nobody authorised.
/// </param>
/// <param name="TaxClasses">Tax classes created or changed.</param>
/// <param name="Categories">Categories created or changed.</param>
/// <param name="Settings">
/// Sent on the <b>first page only</b>, and null on continuations — it is not incremental and
/// repeating it on every page of a large catalog would be noise.
/// </param>
/// <param name="Watermark">
/// Pass back as <c>?since=</c> next time. <b>The server's value, never the client's clock</b>:
/// a till whose clock is fast would otherwise skip everything changed in the gap.
/// </param>
/// <param name="NextProductCursor">Non-null while more products remain.</param>
/// <param name="NextBarcodeCursor">Non-null while more barcodes remain.</param>
/// <remarks>
/// <b>Two cursors, not one.</b> Products and barcodes are different tables with different row
/// counts, and a single position cannot mean "after here" in both — a shared cursor either
/// replays rows from the shorter collection or steps over rows in the longer one, depending on
/// which way it is derived. Two independent keysets are each correct on their own terms.
/// <para>
/// The client walks until <b>both</b> are null and only then commits the watermark. Committing
/// while either remains would skip everything on the pages it had not fetched, permanently.
/// </para>
/// </remarks>
public sealed record CatalogSyncResponse(
    IReadOnlyList<SyncProductResponse> Products,
    IReadOnlyList<SyncBarcodeResponse> Barcodes,
    IReadOnlyList<Guid> RemovedBarcodeIds,
    IReadOnlyList<SyncTaxClassResponse> TaxClasses,
    IReadOnlyList<SyncCategoryResponse> Categories,
    SyncSettingsResponse? Settings,
    DateTimeOffset Watermark,
    string? NextProductCursor,
    string? NextBarcodeCursor);

/// <summary>
/// The catalog as an incremental feed, for the offline mirror.
/// </summary>
/// <remarks>
/// <b>Why this exists rather than reusing the CRUD endpoints.</b> <c>GET /products</c> has no
/// "changed since" filter, barcodes are readable only one product at a time — ten thousand
/// products would be ten thousand requests — and a hard-deleted barcode is invisible to any
/// incremental feed at all, which is what Phase 9's soft delete is for.
/// <para>
/// <b>The feed may repeat a row. It cannot skip one.</b> Worth stating, because a client that
/// assumed otherwise would be wrong in a way nothing would surface. The sort key is
/// <c>COALESCE(updated_at, created_at)</c>, which changes when a row is edited — so a product
/// edited while a till is mid-walk moves <i>ahead</i> of the cursor and is served twice. It can
/// only ever move ahead, because <c>UpdatedAt</c> increases monotonically, so the failure has
/// exactly one direction and it is the harmless one: a duplicate costs the mirror a redundant
/// put, where a skip would be a product it never heard about.
/// </para>
/// <para>
/// The same asymmetry is why the client must make its writes <b>upserts</b> rather than
/// inserts, and why re-asking from slightly before the watermark
/// (<see cref="WatermarkOverlapSeconds"/>) costs nothing.
/// </para>
/// </remarks>
public static class CatalogSyncEndpoints
{
    /// <summary>
    /// How far back a client should rewind its watermark before asking again.
    /// </summary>
    /// <remarks>
    /// <b>Advertised so a client does not have to invent it.</b> A row's
    /// <c>COALESCE(updated_at, created_at)</c> is stamped when <c>SaveChanges</c> runs, but the
    /// row becomes visible only when its transaction commits — so a write that started before
    /// the watermark was read can appear after it, with an earlier timestamp, and a client
    /// resuming exactly at the watermark would step over it for ever.
    /// <para>
    /// The remedy is to re-ask from slightly before. It is free because the mirror's writes are
    /// upserts: re-receiving a row it already has costs a put and changes nothing.
    /// </para>
    /// </remarks>
    public const int WatermarkOverlapSeconds = 30;

    /// <summary>
    /// The sort keys. One per collection, so a product cursor cannot be replayed as a barcode
    /// cursor — <c>PageCursor</c> carries the sort name and refuses a mismatch.
    /// </summary>
    private const string ProductSort = "catalog-sync:product";

    private const string BarcodeSort = "catalog-sync:barcode";

    public static IEndpointRouteBuilder MapCatalogSyncEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var sync = builder.MapGroup("/api/v1/catalog").WithTags("Catalog sync");

        // CanSell: the caller is a till building the mirror it sells from. Everything in the
        // response is already readable by a Cashier through the CRUD endpoints — this is the
        // same data in one round trip rather than ten thousand.
        sync.MapGet("/sync", SyncAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Catalog changes since a watermark, for the offline mirror");

        return builder;
    }

    private static async Task<Results<Ok<CatalogSyncResponse>, ValidationProblem>> SyncAsync(
        AppDbContext db,
        TimeProvider timeProvider,
        string? since,
        string? productCursor,
        string? barcodeCursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Both read before either can short-circuit, so a client that got both cursors wrong
        // is told about both at once — the same rule every list endpoint here follows.
        var validProducts = PageQuery.TryRead<DateTimeOffset>(
            productCursor, limit, ProductSort, out var productPage, out var errors);

        var validBarcodes = PageQuery.TryRead<DateTimeOffset>(
            barcodeCursor, limit, BarcodeSort, out var barcodePage, out var barcodeErrors);

        if (!validProducts || !validBarcodes)
        {
            foreach (var (field, messages) in barcodeErrors)
            {
                // `limit` is validated by both calls and would otherwise be reported twice.
                errors[field == "cursor" ? "barcodeCursor" : field] = messages;
            }

            if (errors.Remove("cursor", out var cursorErrors))
            {
                errors["productCursor"] = cursorErrors;
            }

            return TypedResults.ValidationProblem(errors);
        }

        DateTimeOffset? changedSince = null;

        if (!string.IsNullOrWhiteSpace(since))
        {
            if (!DateTimeOffset.TryParse(
                    since,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["since"] = ["An ISO-8601 timestamp is required. Omit it for a full download."],
                });
            }

            changedSince = parsed.ToUniversalTime();
        }

        /*
         * Read BEFORE the queries, not after.
         *
         * A watermark taken afterwards would cover writes that landed while this request was
         * running but were not in its results — and the client, resuming from it, would never
         * see them. Taken first it can only ever be conservative: the client re-asks for a
         * little it already has, which its upserts absorb.
         */
        var watermark = timeProvider.GetUtcNow();

        var products = await ProductsAsync(db, changedSince, productPage, cancellationToken);
        var barcodes = await BarcodesAsync(db, changedSince, barcodePage, cancellationToken);
        var removed = await RemovedBarcodeIdsAsync(db, changedSince, cancellationToken);
        var taxClasses = await TaxClassesAsync(db, changedSince, cancellationToken);
        var categories = await CategoriesAsync(db, changedSince, cancellationToken);

        // Settings on the first page only. Not incremental, and repeating a fixed object on
        // every page of a ten-thousand-product catalog is noise the client would discard.
        var settings = productPage.After is null && barcodePage.After is null
            ? await SettingsAsync(db, cancellationToken)
            : null;

        return TypedResults.Ok(new CatalogSyncResponse(
            products.Items,
            barcodes.Items,
            removed,
            taxClasses,
            categories,
            settings,
            watermark,
            products.NextCursor,
            barcodes.NextCursor));
    }

    /// <summary>
    /// Products changed since the watermark, keyset-paged.
    /// </summary>
    /// <remarks>
    /// <b>Inactive products are included, unlike <c>GET /products</c>.</b> That endpoint
    /// defaults to active-only because the register grid must never offer an unsellable item.
    /// A mirror is the opposite case: the deactivation is exactly the change it needs to hear
    /// about, and filtering it out would leave the till selling a withdrawn product for ever.
    /// </remarks>
    private static Task<CursorPage<SyncProductResponse>> ProductsAsync(
        AppDbContext db,
        DateTimeOffset? since,
        PageRequest<DateTimeOffset> page,
        CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking();

        if (since is { } watermark)
        {
            query = query.Where(p => (p.UpdatedAt ?? p.CreatedAt) > watermark);
        }

        // ToPageAsync, not a hand-written keyset. The predicate is a Postgres row-value
        // comparison assembled at runtime — `Guid` has no `>` in C# and EF cannot translate
        // `Guid.CompareTo`, so the obvious expanded form does not even compile. Reusing the
        // helper also inherits the test that asserts the generated SQL, which is what catches
        // EF quietly evaluating the predicate client-side and paging by loading the table.
        return query.ToPageAsync(
            p => p.UpdatedAt ?? p.CreatedAt,
            p => new SyncProductResponse(
                p.Id,
                p.Sku,
                p.Name,
                p.Description,
                p.CategoryId,
                p.TaxClassId,
                (decimal)p.UnitPrice,
                p.Unit,
                p.IsActive,
                p.TrackStock,
                p.UpdatedAt ?? p.CreatedAt),
            page,
            cancellationToken);
    }

    private static Task<CursorPage<SyncBarcodeResponse>> BarcodesAsync(
        AppDbContext db,
        DateTimeOffset? since,
        PageRequest<DateTimeOffset> page,
        CancellationToken cancellationToken)
    {
        var query = db.Barcodes.AsNoTracking().Where(b => b.DeletedAt == null);

        if (since is { } watermark)
        {
            query = query.Where(b => (b.UpdatedAt ?? b.CreatedAt) > watermark);
        }

        return query.ToPageAsync(
            b => b.UpdatedAt ?? b.CreatedAt,
            b => new SyncBarcodeResponse(
                b.Id,
                b.ProductId,
                b.Code,
                b.IsPrimary,
                b.UpdatedAt ?? b.CreatedAt),
            page,
            cancellationToken);
    }

    /// <summary>
    /// Codes withdrawn since the watermark.
    /// </summary>
    /// <remarks>
    /// Ids only — the mirror deletes by key and has no use for the rest of the row.
    /// <para>
    /// <b>Unpaged, deliberately.</b> Withdrawals are rare next to the catalog itself, and a
    /// tombstone list that paged separately from the rows would need a fourth cursor position
    /// for a collection that is usually empty. On a first sync (<c>since</c> omitted) it is
    /// skipped entirely: a mirror being built from nothing has nothing to remove.
    /// </para>
    /// </remarks>
    private static Task<List<Guid>> RemovedBarcodeIdsAsync(
        AppDbContext db,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        if (since is not { } watermark)
        {
            return Task.FromResult(new List<Guid>());
        }

        return db.Barcodes
            .AsNoTracking()
            .Where(b => b.DeletedAt != null && b.DeletedAt > watermark)
            .OrderBy(b => b.DeletedAt)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);
    }

    /// <remarks>
    /// Unpaged, like categories below: a shop has a handful of tax classes, and paging a
    /// collection that never exceeds a page is a cursor to get wrong for no benefit.
    /// </remarks>
    private static Task<List<SyncTaxClassResponse>> TaxClassesAsync(
        AppDbContext db,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var query = db.TaxClasses.AsNoTracking();

        if (since is { } watermark)
        {
            query = query.Where(t => (t.UpdatedAt ?? t.CreatedAt) > watermark);
        }

        return query
            .OrderBy(t => t.Name)
            .Select(t => new SyncTaxClassResponse(
                t.Id,
                t.Name,
                t.Rate,
                t.IsDefault,
                t.UpdatedAt ?? t.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    private static Task<List<SyncCategoryResponse>> CategoriesAsync(
        AppDbContext db,
        DateTimeOffset? since,
        CancellationToken cancellationToken)
    {
        var query = db.Categories.AsNoTracking();

        if (since is { } watermark)
        {
            query = query.Where(c => (c.UpdatedAt ?? c.CreatedAt) > watermark);
        }

        return query
            .OrderBy(c => c.SortOrder)
            .ThenBy(c => c.Name)
            .Select(c => new SyncCategoryResponse(
                c.Id,
                c.Name,
                c.ParentCategoryId,
                c.SortOrder,
                c.IsActive,
                c.UpdatedAt ?? c.CreatedAt))
            .ToListAsync(cancellationToken);
    }

    private static async Task<SyncSettingsResponse?> SettingsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(cancellationToken);

        return tenant is null
            ? null
            : new SyncSettingsResponse(
                tenant.CurrencyCode,
                tenant.TimeZoneId,
                tenant.TaxMode,
                (decimal)tenant.CashRoundingIncrement,
                tenant.BusinessDayStartOffset,
                tenant.AddressLine,
                tenant.TaxNumber,
                tenant.ReceiptHeader,
                tenant.ReceiptFooter);
    }

}
