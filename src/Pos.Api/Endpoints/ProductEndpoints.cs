using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Errors;
using Pos.Api.Observability;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>
/// Every field is nullable, and that is load-bearing rather than lazy.
/// </summary>
/// <remarks>
/// A non-nullable <c>decimal UnitPrice</c> binds an omitted field to <c>0</c> — a perfectly
/// valid price — so "you forgot the price" would silently create a free product. A
/// non-nullable <c>bool TrackStock</c> binds to <c>false</c> and silently untracks a stocked
/// item. Nullable is the only way to tell "absent" from "zero".
/// <para>
/// <c>Unit</c> is a <see cref="string"/>, not the enum: with the string converter installed,
/// an unrecognised name throws during model binding and yields a bare 400 with no body,
/// where parsing it here yields a per-field error like everything else.
/// </para>
/// </remarks>
public sealed record CreateProductRequest(
    string? Sku,
    string? Name,
    string? Description,
    Guid? CategoryId,
    Guid? TaxClassId,
    decimal? UnitPrice,
    decimal? CostPrice,
    string? Unit,
    bool? TrackStock);

/// <summary>
/// A full replacement of the mutable fields. There is no <c>isActive</c>: a PUT never
/// resurrects a deactivated product, which is what <c>/activate</c> is for.
/// </summary>
/// <remarks>
/// <c>unit</c> and <c>trackStock</c> omitted mean <i>unchanged</i>, a deliberate exception to
/// the replace semantics. Binding an absent <c>bool</c> to <c>false</c> would silently untrack
/// stock, and an absent enum to its zero value would turn a weighed product into a countable
/// one — both invisible on the screen that caused them.
/// </remarks>
public sealed record UpdateProductRequest(
    string? Sku,
    string? Name,
    string? Description,
    Guid? CategoryId,
    Guid? TaxClassId,
    decimal? UnitPrice,
    decimal? CostPrice,
    string? Unit,
    bool? TrackStock);

/// <summary>
/// A product as the caller is allowed to see it.
/// </summary>
/// <remarks>
/// <c>costPrice</c> is <b>omitted</b> from the JSON for a caller without
/// <c>CanViewMargins</c>, not sent and hidden — invariant 7, on the grounds that anything
/// reaching the browser is readable. The absence is ambiguous with "no cost recorded", which
/// is harmless: the client already knows its own policy list from <c>GET /auth/me</c>, and
/// the ambiguity resolves in the safe direction.
/// </remarks>
public sealed record ProductResponse(
    Guid Id,
    string Sku,
    string Name,
    string? Description,
    Guid? CategoryId,
    Guid TaxClassId,
    decimal UnitPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CostPrice,
    Unit Unit,
    bool IsActive,
    bool TrackStock,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

/// <summary>A code to add to a product. <c>isPrimary</c> is advisory — see <see cref="Barcode.IsPrimary"/>.</summary>
public sealed record AddBarcodeRequest(string? Code, bool? IsPrimary);

/// <summary>One of a product's codes.</summary>
public sealed record BarcodeResponse(
    Guid Id,
    Guid ProductId,
    string Code,
    bool IsPrimary,
    DateTimeOffset CreatedAt);

/// <summary>
/// Everything the register needs to put a scanned item on a line, in one response.
/// </summary>
/// <remarks>
/// The tax class is included <b>as a rate</b>, not as an id to go and fetch. This is the
/// hottest read in the application — every scan of every sale — and a till that had to make
/// a second call to price the line would pay a round trip per item on a connection that is
/// frequently a shop's broadband.
/// <para>
/// <c>isActive</c> is carried rather than filtered on. A deactivated product that is scanned
/// still resolves, so the register can say "this is not for sale" instead of "unknown item,
/// add it?" — which is how a withdrawn product gets recreated as a duplicate by a cashier
/// trying to be helpful.
/// </para>
/// </remarks>
public sealed record BarcodeLookupResponse(
    Guid BarcodeId,
    string Code,
    bool IsPrimary,
    Guid ProductId,
    string Sku,
    string Name,
    string? Description,
    Guid? CategoryId,
    decimal UnitPrice,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] decimal? CostPrice,
    Unit Unit,
    bool IsActive,
    bool TrackStock,
    Guid TaxClassId,
    string TaxClassName,
    decimal TaxRate);

public static class ProductEndpoints
{
    private const string Sort = "product:name";

    private const string SkuConstraint = "ux_product_tenant_sku";

    private const string BarcodeConstraint = "ux_barcode_tenant_code";

    /// <summary>Longest <c>?q=</c> accepted, so a pathological pattern cannot be handed to the index.</summary>
    /// <summary>Shared with <c>GET /stock</c>, so both refuse an over-long term the same way.</summary>
    internal const int MaxSearchTermLength = 200;

    public static IEndpointRouteBuilder MapProductEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var products = builder.MapGroup("/api/v1/products")
            .WithTags("Products");

        products.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Search and page the catalog");

        products.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("One product");

        // The hot path. A literal segment, so it cannot be shadowed by /{id:guid} — and the
        // guid constraint would refuse a barcode anyway.
        products.MapGet("/by-barcode/{code}", ByBarcodeAsync)
            .RequireAuthorization(Policies.CanSell)
            // Scan-to-line latency, which is what a cashier experiences as "the system is
            // slow" and the first thing to degrade as a catalog grows.
            .AddEndpointFilter<BarcodeLookupMetricsFilter>()
            .WithSummary("Scan a barcode");

        products.MapGet("/{id:guid}/barcodes", ListBarcodesAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("A product's codes");

        products.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Create a product");

        products.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Replace a product");

        // No DELETE verb, and there will not be one: sale lines reference products forever,
        // so a delete either orphans history or cascades a customer's sales away.
        products.MapPost("/{id:guid}/deactivate", DeactivateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Stop selling a product");

        products.MapPost("/{id:guid}/activate", ActivateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Sell a product again");

        products.MapPost("/{id:guid}/barcodes", AddBarcodeAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Add a code to a product");

        // Barcodes may be deleted, unlike products: a mis-scanned label is data entry, not
        // history. Nothing financial points at a barcode — a sale line points at the product.
        products.MapDelete("/{id:guid}/barcodes/{barcodeId:guid}", RemoveBarcodeAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Remove a code from a product");

        return builder;
    }

    private static async Task<Results<Ok<CursorPage<ProductResponse>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        string? cursor,
        int? limit,
        string? q,
        Guid? categoryId,
        bool? activeOnly,
        CancellationToken cancellationToken)
    {
        if (!PageQuery.TryRead<string>(cursor, limit, Sort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var term = q?.Trim();

        if (term is { Length: > MaxSearchTermLength })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["q"] = [$"A search term of at most {MaxSearchTermLength} characters is required."],
            });
        }

        var query = db.Products.AsNoTracking();

        // ANDed, in this order. activeOnly defaults to true because the register grid is the
        // dominant caller and must never offer an unsellable product — so forgetting the
        // parameter fails safe. The catalog admin screen asks for everything, deliberately,
        // in one place.
        if (activeOnly ?? true)
        {
            query = query.Where(p => p.IsActive);
        }

        // A category belonging to another tenant matches nothing and yields an empty page.
        // Not a 404: this is a filter, and answering 404 would confirm the id exists.
        if (categoryId is { } category)
        {
            query = query.Where(p => p.CategoryId == category);
        }

        if (!string.IsNullOrEmpty(term))
        {
            query = query.Where(MatchesSearchTerm(term));
        }

        var canViewMargins = await CanViewMarginsAsync(caller, authorization);

        var results = await query.ToPageAsync(
            p => p.Name,
            canViewMargins ? ProjectWithCost : ProjectWithoutCost,
            page,
            cancellationToken);

        return TypedResults.Ok(results);
    }

    private static async Task<Results<Ok<ProductResponse>, NotFound>> GetAsync(
        Guid id,
        AppDbContext db,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        var canViewMargins = await CanViewMarginsAsync(caller, authorization);

        var product = await db.Products
            .AsNoTracking()
            .Where(p => p.Id == id)
            .Select(canViewMargins ? ProjectWithCost : ProjectWithoutCost)
            .FirstOrDefaultAsync(cancellationToken);

        return product is null ? TypedResults.NotFound() : TypedResults.Ok(product);
    }

    private static async Task<Results<Ok<BarcodeLookupResponse>, NotFound>> ByBarcodeAsync(
        string code,
        AppDbContext db,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        var normalised = Barcode.NormalizeCode(code);

        // A blank or whitespace-only scan is 404, not 400. The caller is a scanner, and every
        // answer this endpoint gives has to be one of the two the register knows how to show:
        // an item, or "unknown item". A validation problem is a third thing it would have to
        // learn to render for a case a human never types.
        if (normalised is null)
        {
            return TypedResults.NotFound();
        }

        var canViewMargins = await CanViewMarginsAsync(caller, authorization);

        var scanned = await LookupQuery(db, normalised, canViewMargins)
            .FirstOrDefaultAsync(cancellationToken);

        return scanned is null ? TypedResults.NotFound() : TypedResults.Ok(scanned);
    }

    private static async Task<Results<Ok<IReadOnlyList<BarcodeResponse>>, NotFound>> ListBarcodesAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var barcodes = await db.Barcodes
            .AsNoTracking()
            .Where(b => b.ProductId == id && b.DeletedAt == null)
            .OrderBy(b => b.Code)
            .Select(b => new BarcodeResponse(b.Id, b.ProductId, b.Code, b.IsPrimary, b.CreatedAt))
            .ToListAsync(cancellationToken);

        // A product with no codes and a product that does not exist both come back empty
        // above, and only the second is a 404. Asked in this order so the extra round trip is
        // paid only in the rare case rather than on every read.
        if (barcodes.Count == 0 && !await db.Products.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok<IReadOnlyList<BarcodeResponse>>(barcodes);
    }

    private static async Task<Results<Created<BarcodeResponse>, NotFound, ValidationProblem>> AddBarcodeAsync(
        Guid id,
        AddBarcodeRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Length is checked before normalising, because NormalizeCode truncates. Normalising
        // first would store a shortened code and report success — and a shortened barcode
        // scans as nothing at all.
        var trimmed = request.Code?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > Barcode.CodeMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["code"] = [$"A barcode of 1 to {Barcode.CodeMaxLength} characters is required."],
            });
        }

        if (!await db.Products.AnyAsync(p => p.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var barcode = new Barcode
        {
            ProductId = id,
            Code = Barcode.NormalizeCode(trimmed)!,

            // Stored as sent. Nothing enforces one primary per product — see Barcode.IsPrimary.
            IsPrimary = request.IsPrimary ?? false,
        };

        db.Barcodes.Add(barcode);

        await SaveOrReportDuplicateBarcodeAsync(db, barcode.Code, cancellationToken);

        // The collection, because there is no single-barcode GET to point at and inventing
        // one to satisfy a header is not worth an endpoint.
        return TypedResults.Created(
            $"/api/v1/products/{id}/barcodes",
            new BarcodeResponse(barcode.Id, barcode.ProductId, barcode.Code, barcode.IsPrimary, barcode.CreatedAt));
    }

    private static async Task<Results<NoContent, NotFound>> RemoveBarcodeAsync(
        Guid id,
        Guid barcodeId,
        AppDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        // Matched on both ids, and on the code not already being withdrawn. A code addressed
        // under the wrong product is a 404 rather than a delete of somebody else's barcode
        // that happened to be named correctly.
        var barcode = await db.Barcodes
            .FirstOrDefaultAsync(
                b => b.Id == barcodeId && b.ProductId == id && b.DeletedAt == null,
                cancellationToken);

        // 404 on a second delete rather than 204-always. Removing a code that is not there
        // means the client is working from a stale list, which is worth it seeing.
        if (barcode is null)
        {
            return TypedResults.NotFound();
        }

        /*
         * Stamped, not deleted — since Phase 9, and for the offline mirror rather than for an
         * audit trail.
         *
         * A till syncs its catalog by asking what changed since it last looked. A row that has
         * been removed outright answers nothing at all, so the withdrawal would never reach the
         * till and it would go on scanning a code the shop had retired, at a price nobody
         * authorised, until somebody thought to rebuild the mirror. A tombstone is the only
         * thing a `?since=` feed can carry a removal as.
         *
         * The unique index is filtered on `deleted_at IS NULL`, so the code can be added again
         * afterwards — which is the ordinary case: a label was mis-typed and is being corrected.
         */
        barcode.DeletedAt = timeProvider.GetUtcNow();

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    private static async Task<Results<Created<ProductResponse>, ValidationProblem>> CreateAsync(
        CreateProductRequest request,
        AppDbContext db,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = await ValidateAsync(
            db,
            request.Sku,
            request.Name,
            request.Description,
            request.CategoryId,
            request.TaxClassId,
            request.UnitPrice,
            request.CostPrice,
            request.Unit,
            cancellationToken);

        if (fields.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(fields.Errors);
        }

        var canViewMargins = await CanViewMarginsAsync(caller, authorization);

        var product = new Product
        {
            Sku = fields.Sku,
            Name = fields.Name,
            Description = fields.Description,
            CategoryId = request.CategoryId,
            TaxClassId = request.TaxClassId!.Value,
            UnitPrice = (Money)request.UnitPrice!.Value,

            // A field a role may not read is a field it may not write. A Manager's create is
            // stored with no cost rather than refused, for the same reason a TenantId in a
            // body is ignored rather than rejected — see ForgedTenancyTests.
            CostPrice = canViewMargins ? (Money?)request.CostPrice : null,
            Unit = fields.Unit ?? Core.Entities.Unit.Each,
            TrackStock = request.TrackStock ?? true,
        };

        db.Products.Add(product);

        await SaveOrReportDuplicateSkuAsync(db, fields.Sku, cancellationToken);

        return TypedResults.Created($"/api/v1/products/{product.Id}", Map(product, canViewMargins));
    }

    private static async Task<Results<Ok<ProductResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateProductRequest request,
        AppDbContext db,
        ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = await ValidateAsync(
            db,
            request.Sku,
            request.Name,
            request.Description,
            request.CategoryId,
            request.TaxClassId,
            request.UnitPrice,
            request.CostPrice,
            request.Unit,
            cancellationToken);

        if (fields.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(fields.Errors);
        }

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return TypedResults.NotFound();
        }

        var canViewMargins = await CanViewMarginsAsync(caller, authorization);

        product.Sku = fields.Sku;
        product.Name = fields.Name;

        // Omitted means null: PUT replaces, and there is no PATCH.
        product.Description = fields.Description;
        product.CategoryId = request.CategoryId;
        product.TaxClassId = request.TaxClassId!.Value;
        product.UnitPrice = (Money)request.UnitPrice!.Value;

        // Left alone for a caller who cannot see it. Their GET omits costPrice, so a
        // read-modify-write round trip sends it back absent — and full-replace semantics
        // would wipe the Owner's cost data on every edit a Manager made.
        if (canViewMargins)
        {
            product.CostPrice = (Money?)request.CostPrice;
        }

        // The two exceptions to full replacement. See UpdateProductRequest.
        product.Unit = fields.Unit ?? product.Unit;
        product.TrackStock = request.TrackStock ?? product.TrackStock;

        await SaveOrReportDuplicateSkuAsync(db, fields.Sku, cancellationToken);

        return TypedResults.Ok(Map(product, canViewMargins));
    }

    private static Task<Results<NoContent, NotFound>> DeactivateAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken) =>
        SetActiveAsync(id, isActive: false, db, cancellationToken);

    private static Task<Results<NoContent, NotFound>> ActivateAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken) =>
        SetActiveAsync(id, isActive: true, db, cancellationToken);

    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        Guid id,
        bool isActive,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);

        if (product is null)
        {
            return TypedResults.NotFound();
        }

        product.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Saves, turning the SKU unique violation into a domain error and leaving every other
    /// constraint failure to become a 500.
    /// </summary>
    /// <remarks>
    /// Caught rather than pre-checked. "Does this SKU exist yet?" is check-then-act: two
    /// concurrent creates both pass it and one still reaches the index, so a pre-check cannot
    /// remove the case — only make it rare enough to appear in production and never in a
    /// test — while costing a round trip on every write.
    /// </remarks>
    private static async Task SaveOrReportDuplicateSkuAsync(
        AppDbContext db,
        string sku,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception, SkuConstraint))
        {
            throw DuplicateSkuException.ForSku(sku, exception);
        }
    }

    /// <summary>
    /// Saves, turning the barcode unique violation into a domain error.
    /// </summary>
    /// <remarks>
    /// The constraint is named, not inferred from the <c>23505</c> alone: <c>product</c> and
    /// <c>barcode</c> both carry unique indexes, and matching on the code alone would report
    /// whichever one fired as whatever the nearest catch block happened to be about.
    /// </remarks>
    private static async Task SaveOrReportDuplicateBarcodeAsync(
        AppDbContext db,
        string code,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (PostgresErrors.IsUniqueViolation(exception, BarcodeConstraint))
        {
            throw DuplicateBarcodeException.ForCode(code, exception);
        }
    }

    /// <summary>
    /// The scan, as one query: barcode joined to its product joined to that product's tax
    /// class.
    /// </summary>
    /// <remarks>
    /// <b>Internal so a test can call the real thing.</b> The property this has to hold is
    /// about the generated SQL — one round trip, no correlated subquery per row — and a test
    /// that rebuilt an equivalent query would assert that the shape is expressible rather
    /// than that the endpoint uses it.
    /// <para>
    /// The join is explicit because there are no navigation properties to walk (see
    /// DECISIONS.md): with them, <c>Include</c> would be the obvious thing to write and would
    /// produce either a second query or a wider one.
    /// </para>
    /// </remarks>
    internal static IQueryable<BarcodeLookupResponse> LookupQuery(
        AppDbContext db,
        string code,
        bool canViewMargins) =>
        canViewMargins ? LookupWithCost(db, code) : LookupWithoutCost(db, code);

    private static IQueryable<BarcodeLookupResponse> LookupWithCost(AppDbContext db, string code) =>
        from barcode in db.Barcodes.AsNoTracking()
        join product in db.Products on barcode.ProductId equals product.Id
        join taxClass in db.TaxClasses on product.TaxClassId equals taxClass.Id
        // A withdrawn code must not scan. The row survives as a tombstone so the offline
        // mirror learns the code is gone; it is not a code the shop still sells against.
        where barcode.Code == code && barcode.DeletedAt == null
        select new BarcodeLookupResponse(
            barcode.Id,
            barcode.Code,
            barcode.IsPrimary,
            product.Id,
            product.Sku,
            product.Name,
            product.Description,
            product.CategoryId,
            (decimal)product.UnitPrice,
            (decimal?)product.CostPrice,
            product.Unit,
            product.IsActive,
            product.TrackStock,
            taxClass.Id,
            taxClass.Name,
            taxClass.Rate);

    /// <summary>The same query with the cost column not named. See <see cref="ProjectWithoutCost"/>.</summary>
    private static IQueryable<BarcodeLookupResponse> LookupWithoutCost(AppDbContext db, string code) =>
        from barcode in db.Barcodes.AsNoTracking()
        join product in db.Products on barcode.ProductId equals product.Id
        join taxClass in db.TaxClasses on product.TaxClassId equals taxClass.Id
        // A withdrawn code must not scan. The row survives as a tombstone so the offline
        // mirror learns the code is gone; it is not a code the shop still sells against.
        where barcode.Code == code && barcode.DeletedAt == null
        select new BarcodeLookupResponse(
            barcode.Id,
            barcode.Code,
            barcode.IsPrimary,
            product.Id,
            product.Sku,
            product.Name,
            product.Description,
            product.CategoryId,
            (decimal)product.UnitPrice,
            null,
            product.Unit,
            product.IsActive,
            product.TrackStock,
            taxClass.Id,
            taxClass.Name,
            taxClass.Rate);

    private sealed record ValidatedFields(
        string Sku,
        string Name,
        string? Description,
        Unit? Unit,
        Dictionary<string, string[]> Errors);

    /// <summary>
    /// Checks every field before returning, so a request wrong in three places is told about
    /// all three.
    /// </summary>
    private static async Task<ValidatedFields> ValidateAsync(
        AppDbContext db,
        string? sku,
        string? name,
        string? description,
        Guid? categoryId,
        Guid? taxClassId,
        decimal? unitPrice,
        decimal? costPrice,
        string? unit,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        var trimmedSku = sku?.Trim() ?? string.Empty;

        // Length is checked *before* normalising, because NormalizeSku truncates. Normalising
        // first would silently store a shortened SKU and report success.
        if (trimmedSku.Length is 0 or > Product.SkuMaxLength)
        {
            errors["sku"] = [$"A SKU of 1 to {Product.SkuMaxLength} characters is required."];
        }

        var trimmedName = name?.Trim() ?? string.Empty;

        if (trimmedName.Length is 0 or > Product.NameMaxLength)
        {
            errors["name"] = [$"A name of 1 to {Product.NameMaxLength} characters is required."];
        }

        var trimmedDescription = description?.Trim();

        if (trimmedDescription is { Length: > Product.DescriptionMaxLength })
        {
            errors["description"] = [$"A description of at most {Product.DescriptionMaxLength} characters is required."];
        }

        if (unitPrice is not { } price)
        {
            errors["unitPrice"] = ["A unit price is required."];
        }
        else if (!CatalogRules.IsStorableAmount(price))
        {
            errors["unitPrice"] = ["A price of 0 or more with at most 4 decimal places is required."];
        }

        // Validated even when it will be ignored, so a caller who cannot see margins still
        // gets told their number was malformed rather than having it silently dropped.
        if (costPrice is { } cost && !CatalogRules.IsStorableAmount(cost))
        {
            errors["costPrice"] = ["A cost of 0 or more with at most 4 decimal places is required."];
        }

        Unit? parsedUnit = null;

        if (!string.IsNullOrWhiteSpace(unit))
        {
            if (Enum.TryParse<Unit>(unit, ignoreCase: false, out var value) && Enum.IsDefined(value))
            {
                parsedUnit = value;
            }
            else
            {
                errors["unit"] = [$"One of {string.Join(", ", Enum.GetNames<Unit>())} is required."];
            }
        }

        if (taxClassId is not { } taxClass || taxClass == Guid.Empty)
        {
            // Required: a product with no tax class cannot be priced at all.
            errors["taxClassId"] = ["A tax class is required."];
        }
        else if (!await db.TaxClasses.AnyAsync(t => t.Id == taxClass, cancellationToken))
        {
            // 400 on the field rather than 404, and the same answer whether the id is
            // unknown or belongs to another shop — so it is not an existence oracle.
            errors["taxClassId"] = ["No tax class with that id exists in this tenant."];
        }

        if (categoryId is { } category
            && !await db.Categories.AnyAsync(c => c.Id == category, cancellationToken))
        {
            errors["categoryId"] = ["No category with that id exists in this tenant."];
        }

        return new ValidatedFields(
            Product.NormalizeSku(trimmedSku) ?? string.Empty,
            trimmedName,
            string.IsNullOrEmpty(trimmedDescription) ? null : trimmedDescription,
            parsedUnit,
            errors);
    }

    /// <summary>
    /// What <c>?q=</c> matches — <b>the one definition</b>, shared with <c>GET /stock</c>.
    /// </summary>
    /// <remarks>
    /// Name is a case-insensitive contains, served by <c>ix_product_tenant_name_trgm</c>. SKU is
    /// an exact match on the normalised term against <c>ux_product_tenant_sku</c>: SKUs are
    /// short, trigrams need three non-wildcard characters to be selective, and what staff do
    /// with a SKU is type or scan the whole thing.
    /// <para>
    /// Shared rather than copied because the stock screen and the catalog screen search the same
    /// products, and two implementations would eventually disagree — a shopkeeper would find
    /// that one screen locates an item the other cannot, which reads as data missing rather than
    /// as a search behaving differently.
    /// </para>
    /// </remarks>
    internal static Expression<Func<Product, bool>> MatchesSearchTerm(string term)
    {
        // Escaped before it becomes a pattern. Without this, q=% matches every product and q=_
        // matches every single-character name — the caller would be writing the LIKE pattern
        // rather than searching with it.
        var pattern = $"%{Escape(term)}%";
        var sku = Product.NormalizeSku(term);

        return p => EF.Functions.ILike(p.Name, pattern, "\\") || p.Sku == sku;
    }

    /// <summary>
    /// Escapes the characters <c>LIKE</c> treats as wildcards, so a search term is a search
    /// term. The backslash is first, or it would escape the escapes added after it.
    /// </summary>
    private static string Escape(string term) => term
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>
    /// Whether the caller may see cost and margin figures.
    /// </summary>
    /// <remarks>
    /// Asked of the authorization service by policy name, never derived from a role literal —
    /// <c>AuthorizationContractTests.No_endpoint_tests_a_role_literal</c> scans <c>src/</c>
    /// for exactly that and fails the build over it.
    /// </remarks>
    private static async Task<bool> CanViewMarginsAsync(
        ClaimsPrincipal caller,
        IAuthorizationService authorization) =>
        (await authorization.AuthorizeAsync(caller, Policies.CanViewMargins)).Succeeded;

    /// <summary>
    /// Two projections rather than one with a conditional.
    /// </summary>
    /// <remarks>
    /// <c>canView ? p.CostPrice : null</c> inside a single projection compiles to
    /// <c>CASE WHEN @p THEN cost_price ELSE NULL END</c>, which still reads the column. The
    /// value would never leave the server, so the invariant would hold — but it is weaker
    /// than <c>EmployeeEndpoints</c>' "the columns never leave the database", and it leaves
    /// nothing to assert against. With two expressions a Cashier's SQL genuinely does not
    /// mention <c>cost_price</c>.
    /// </remarks>
    private static Expression<Func<Product, ProductResponse>> ProjectWithCost =>
        p => new ProductResponse(
            p.Id, p.Sku, p.Name, p.Description, p.CategoryId, p.TaxClassId, (decimal)p.UnitPrice,
            (decimal?)p.CostPrice, p.Unit, p.IsActive, p.TrackStock, p.CreatedAt, p.UpdatedAt);

    private static Expression<Func<Product, ProductResponse>> ProjectWithoutCost =>
        p => new ProductResponse(
            p.Id, p.Sku, p.Name, p.Description, p.CategoryId, p.TaxClassId, (decimal)p.UnitPrice,
            null, p.Unit, p.IsActive, p.TrackStock, p.CreatedAt, p.UpdatedAt);

    private static ProductResponse Map(Product product, bool canViewMargins) => new(
        product.Id,
        product.Sku,
        product.Name,
        product.Description,
        product.CategoryId,
        product.TaxClassId,
        product.UnitPrice.ToDecimal(),
        canViewMargins ? product.CostPrice?.ToDecimal() : null,
        product.Unit,
        product.IsActive,
        product.TrackStock,
        product.CreatedAt,
        product.UpdatedAt);
}
