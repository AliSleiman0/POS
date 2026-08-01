using System.Linq.Expressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Data;

namespace Pos.Api.Endpoints;

public sealed record CreateCategoryRequest(string? Name, Guid? ParentCategoryId, int? SortOrder);

/// <summary>
/// A full replacement. <c>parentCategoryId</c> omitted moves the category to the top level,
/// which is what "replace" means — there is no PATCH.
/// </summary>
public sealed record UpdateCategoryRequest(string? Name, Guid? ParentCategoryId, int? SortOrder);

public sealed record CategoryResponse(
    Guid Id,
    string Name,
    Guid? ParentCategoryId,
    int SortOrder,
    bool IsActive,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public static class CategoryEndpoints
{
    private const string Sort = "category:name";

    public static IEndpointRouteBuilder MapCategoryEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var categories = builder.MapGroup("/api/v1/categories")
            .WithTags("Categories");

        categories.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Every category in this tenant");

        categories.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Create a category");

        categories.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Replace a category");

        categories.MapPost("/{id:guid}/deactivate", DeactivateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Hide a category from pickers");

        categories.MapPost("/{id:guid}/activate", ActivateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Show a category again");

        return builder;
    }

    private static async Task<Results<Ok<CursorPage<CategoryResponse>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        string? cursor,
        int? limit,
        bool? activeOnly,
        CancellationToken cancellationToken)
    {
        if (!PageQuery.TryRead<string>(cursor, limit, Sort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.Categories.AsNoTracking();

        // Defaults to true: forgetting the parameter hides deactivated rows, which is the
        // safe direction for the pickers that read this. The admin screen asks for
        // everything deliberately, in one place.
        if (activeOnly ?? true)
        {
            query = query.Where(c => c.IsActive);
        }

        // Ordered by (name, id), not SortOrder. A keyset needs a total order over an indexed
        // column and SortOrder is neither unique nor indexed; it is returned so the client
        // can arrange its own picker by it.
        var results = await query.ToPageAsync(c => c.Name, Project, page, cancellationToken);

        return TypedResults.Ok(results);
    }

    private static async Task<Results<Created<CategoryResponse>, ValidationProblem>> CreateAsync(
        CreateCategoryRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, errors) = Validate(request.Name, request.SortOrder);

        var hierarchy = await HierarchyAsync(db, cancellationToken);

        if (request.ParentCategoryId is { } parent && !hierarchy.ContainsKey(parent))
        {
            // 400 on the field, not 404. The request is what is wrong, and the answer is the
            // same whether the id does not exist or belongs to another shop — so it is not
            // an existence oracle either.
            errors["parentCategoryId"] = ["No category with that id exists in this tenant."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var category = new Category
        {
            Name = name,
            ParentCategoryId = request.ParentCategoryId,
            SortOrder = request.SortOrder ?? 0,
        };

        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created($"/api/v1/categories/{category.Id}", Map(category));
    }

    private static async Task<Results<Ok<CategoryResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateCategoryRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, errors) = Validate(request.Name, request.SortOrder);

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category is null)
        {
            return TypedResults.NotFound();
        }

        var hierarchy = await HierarchyAsync(db, cancellationToken);

        if (request.ParentCategoryId is { } parent)
        {
            if (!hierarchy.ContainsKey(parent))
            {
                errors["parentCategoryId"] = ["No category with that id exists in this tenant."];
            }
            else if (CategoryHierarchy.WouldCreateCycle(hierarchy, id, parent))
            {
                // Thrown rather than added to the errors map: a cycle is not a malformed
                // field, it is a statement about the shape of the tree, and it gets its own
                // stable `type` slug so a client can say something specific about it.
                throw new CategoryCycleException(
                    "That parent is the category itself or sits beneath it, which would make "
                    + "the category its own ancestor.");
            }
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        category.Name = name;

        // Omitted means top level. PUT replaces, and there is no PATCH — this is the one
        // place that is worth stating out loud, because it is how a client that forgets to
        // send the parent flattens its own tree.
        category.ParentCategoryId = request.ParentCategoryId;
        category.SortOrder = request.SortOrder ?? 0;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(Map(category));
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

    /// <summary>
    /// Flips <c>IsActive</c>. Idempotent: deactivating a deactivated category is a 204.
    /// </summary>
    /// <remarks>
    /// Deactivating does <b>not</b> cascade to the category's products or children. The
    /// category stops appearing in pickers; products keep their <c>CategoryId</c>, because
    /// a cascade would silently make a shelf-worth of stock unsellable and there would be
    /// nothing on the screen that said so.
    /// </remarks>
    private static async Task<Results<NoContent, NotFound>> SetActiveAsync(
        Guid id,
        bool isActive,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (category is null)
        {
            return TypedResults.NotFound();
        }

        category.IsActive = isActive;
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// The whole tenant's parent map, in one read.
    /// </summary>
    /// <remarks>
    /// One query rather than a loop walking the chain a level at a time: that would be N
    /// round trips to answer a question about a table holding dozens of rows, and it would
    /// read the hierarchy at N different instants. It doubles as the existence check for a
    /// proposed parent, so the cycle test costs nothing extra.
    /// </remarks>
    private static Task<Dictionary<Guid, Guid?>> HierarchyAsync(
        AppDbContext db,
        CancellationToken cancellationToken) =>
        db.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.ParentCategoryId })
            .ToDictionaryAsync(c => c.Id, c => c.ParentCategoryId, cancellationToken);

    private static (string Name, Dictionary<string, string[]> Errors) Validate(string? name, int? sortOrder)
    {
        var errors = new Dictionary<string, string[]>();
        var trimmed = name?.Trim() ?? string.Empty;

        if (trimmed.Length is 0 or > Category.NameMaxLength)
        {
            errors["name"] = [$"A name of 1 to {Category.NameMaxLength} characters is required."];
        }

        if (sortOrder is < 0)
        {
            errors["sortOrder"] = ["A sort order of 0 or more is required."];
        }

        return (trimmed, errors);
    }

    private static Expression<Func<Category, CategoryResponse>> Project =>
        c => new CategoryResponse(
            c.Id, c.Name, c.ParentCategoryId, c.SortOrder, c.IsActive, c.CreatedAt, c.UpdatedAt);

    private static CategoryResponse Map(Category category) => new(
        category.Id,
        category.Name,
        category.ParentCategoryId,
        category.SortOrder,
        category.IsActive,
        category.CreatedAt,
        category.UpdatedAt);
}
