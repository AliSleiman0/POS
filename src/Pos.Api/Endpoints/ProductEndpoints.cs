using System.Linq.Expressions;
using System.Security.Claims;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Errors;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
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

public static class ProductEndpoints
{
    private const string Sort = "product:name";

    private const string SkuConstraint = "ux_product_tenant_sku";

    /// <summary>Longest <c>?q=</c> accepted, so a pathological pattern cannot be handed to the index.</summary>
    private const int MaxSearchTermLength = 200;

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
            // Escaped before it becomes a pattern. Without this, q=% matches every product
            // and q=_ matches every single-character name — the caller would be writing the
            // LIKE pattern rather than searching with it.
            var pattern = $"%{Escape(term)}%";

            // Name is a case-insensitive contains, served by ix_product_tenant_name_trgm.
            // SKU is an exact match on the normalised term against ux_product_tenant_sku:
            // SKUs are short, trigrams need three non-wildcard characters to be selective,
            // and what staff actually do with a SKU is type or scan the whole thing.
            var sku = Product.NormalizeSku(term);

            query = query.Where(p => EF.Functions.ILike(p.Name, pattern, "\\") || p.Sku == sku);
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
            UnitPrice = request.UnitPrice!.Value,

            // A field a role may not read is a field it may not write. A Manager's create is
            // stored with no cost rather than refused, for the same reason a TenantId in a
            // body is ignored rather than rejected — see ForgedTenancyTests.
            CostPrice = canViewMargins ? request.CostPrice : null,
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
        product.UnitPrice = request.UnitPrice!.Value;

        // Left alone for a caller who cannot see it. Their GET omits costPrice, so a
        // read-modify-write round trip sends it back absent — and full-replace semantics
        // would wipe the Owner's cost data on every edit a Manager made.
        if (canViewMargins)
        {
            product.CostPrice = request.CostPrice;
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
            p.Id, p.Sku, p.Name, p.Description, p.CategoryId, p.TaxClassId, p.UnitPrice,
            p.CostPrice, p.Unit, p.IsActive, p.TrackStock, p.CreatedAt, p.UpdatedAt);

    private static Expression<Func<Product, ProductResponse>> ProjectWithoutCost =>
        p => new ProductResponse(
            p.Id, p.Sku, p.Name, p.Description, p.CategoryId, p.TaxClassId, p.UnitPrice,
            null, p.Unit, p.IsActive, p.TrackStock, p.CreatedAt, p.UpdatedAt);

    private static ProductResponse Map(Product product, bool canViewMargins) => new(
        product.Id,
        product.Sku,
        product.Name,
        product.Description,
        product.CategoryId,
        product.TaxClassId,
        product.UnitPrice,
        canViewMargins ? product.CostPrice : null,
        product.Unit,
        product.IsActive,
        product.TrackStock,
        product.CreatedAt,
        product.UpdatedAt);
}
