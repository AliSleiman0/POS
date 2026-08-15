using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Orders;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>One answer the till can offer, with the price it adds.</summary>
public sealed record ModifierOptionResponse(
    Guid Id,
    Guid ProductId,
    string Name,
    decimal UnitPrice,
    int SortOrder,
    bool IsDefault);

/// <summary>A question the till asks, and its answers.</summary>
public sealed record ModifierGroupResponse(
    Guid Id,
    string Name,
    int MinSelections,
    int? MaxSelections,
    int SortOrder,
    IReadOnlyList<ModifierOptionResponse> Options);

/// <summary>Create or edit a question.</summary>
public sealed record SaveModifierGroupRequest(
    string? Name,
    int? MinSelections,
    int? MaxSelections,
    int? SortOrder,
    bool? IsActive,
    IReadOnlyList<Guid>? OptionProductIds);

/// <summary>Which questions an item asks.</summary>
public sealed record AssignModifierGroupsRequest(IReadOnlyList<Guid>? ModifierGroupIds);

/// <summary>
/// The menu's questions: what the till asks when an item is ordered, and what it may answer.
/// </summary>
/// <remarks>
/// <b>The options are products.</b> "Extra cheese" is a catalog row with a price, a tax class and
/// optional stock tracking, flagged <c>IsModifier</c> so it stays out of the register grid — so
/// the price shown on the sheet and the price charged on the bill are the same number by
/// construction rather than by two things being kept in step.
/// <para>
/// Reads are <c>CanTakeOrders</c>, because every order screen makes them. Writes are
/// <c>CanManageCatalog</c>, because this is the menu: changing what "Cooked how?" offers is the
/// same authority as changing a price.
/// </para>
/// </remarks>
public static class MenuEndpoints
{
    public static IEndpointRouteBuilder MapMenuEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var menu = builder.MapGroup("/api/v1/menu")
            .WithTags("Menu")
            .RequireRestaurantMode();

        menu.MapGet("/modifier-groups", ListGroupsAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("Every question the menu can ask, with its answers");

        menu.MapGet("/products/{productId:guid}/modifier-groups", ForProductAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("The questions one item asks, in order");

        menu.MapPost("/modifier-groups", CreateGroupAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Add a question");

        menu.MapPut("/modifier-groups/{id:guid}", UpdateGroupAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Change a question or its answers");

        menu.MapPut("/products/{productId:guid}/modifier-groups", AssignAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Set which questions an item asks");

        return builder;
    }

    private static async Task<Ok<IReadOnlyList<ModifierGroupResponse>>> ListGroupsAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var groups = await db.ModifierGroups
            .AsNoTracking()
            .Where(g => g.IsActive)
            .OrderBy(g => g.SortOrder).ThenBy(g => g.Name)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(await ProjectAsync(db, groups, cancellationToken));
    }

    private static async Task<Results<Ok<IReadOnlyList<ModifierGroupResponse>>, NotFound>> ForProductAsync(
        Guid productId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // Scoped by the query filter, so another tenant's product is a 404 indistinguishable
        // from one that does not exist.
        if (!await db.Products.AnyAsync(p => p.Id == productId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var links = await db.ProductModifierGroups
            .AsNoTracking()
            .Where(link => link.ProductId == productId)
            .OrderBy(link => link.SortOrder)
            .ToListAsync(cancellationToken);

        var ids = links.Select(link => link.ModifierGroupId).ToArray();

        var groups = await db.ModifierGroups
            .AsNoTracking()
            .Where(g => ids.Contains(g.Id) && g.IsActive)
            .ToListAsync(cancellationToken);

        // Ordered by the link, not by the group: "Cooked how?" comes before "Any sides?" on a
        // steak and might not on something else, which is why the order lives on the pairing.
        var ordered = links
            .Select(link => groups.FirstOrDefault(g => g.Id == link.ModifierGroupId))
            .Where(g => g is not null)
            .Select(g => g!)
            .ToList();

        return TypedResults.Ok(await ProjectAsync(db, ordered, cancellationToken));
    }

    private static async Task<Results<Created<ModifierGroupResponse>, ValidationProblem>> CreateGroupAsync(
        SaveModifierGroupRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, errors) = await ValidateAsync(request, db, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var group = new ModifierGroup
        {
            Name = name!,
            MinSelections = request.MinSelections ?? 0,
            MaxSelections = request.MaxSelections,
            SortOrder = request.SortOrder ?? 0,
            IsActive = request.IsActive ?? true,
        };

        db.ModifierGroups.Add(group);
        await db.SaveChangesAsync(cancellationToken);

        await ReplaceOptionsAsync(db, group.Id, request.OptionProductIds ?? [], cancellationToken);

        var projected = await ProjectAsync(db, [group], cancellationToken);

        return TypedResults.Created($"/api/v1/menu/modifier-groups/{group.Id}", projected[0]);
    }

    private static async Task<Results<Ok<ModifierGroupResponse>, NotFound, ValidationProblem>> UpdateGroupAsync(
        Guid id,
        SaveModifierGroupRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, errors) = await ValidateAsync(request, db, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var group = await db.ModifierGroups.FirstOrDefaultAsync(g => g.Id == id, cancellationToken);

        if (group is null)
        {
            return TypedResults.NotFound();
        }

        group.Name = name!;
        group.MinSelections = request.MinSelections ?? group.MinSelections;
        group.MaxSelections = request.MaxSelections;
        group.SortOrder = request.SortOrder ?? group.SortOrder;
        group.IsActive = request.IsActive ?? group.IsActive;

        await db.SaveChangesAsync(cancellationToken);

        if (request.OptionProductIds is not null)
        {
            await ReplaceOptionsAsync(db, group.Id, request.OptionProductIds, cancellationToken);
        }

        var projected = await ProjectAsync(db, [group], cancellationToken);

        return TypedResults.Ok(projected[0]);
    }

    private static async Task<Results<Ok<IReadOnlyList<ModifierGroupResponse>>, NotFound, ValidationProblem>>
        AssignAsync(
            Guid productId,
            AssignModifierGroupsRequest request,
            AppDbContext db,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!await db.Products.AnyAsync(p => p.Id == productId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var wanted = request.ModifierGroupIds ?? [];

        var known = await db.ModifierGroups
            .Where(g => wanted.Contains(g.Id))
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        if (known.Count != wanted.Distinct().Count())
        {
            // 400 on the field rather than 404, identical whether a group is unknown or another
            // shop's, so it is not an existence oracle.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["modifierGroupIds"] = ["One or more of those questions does not exist in this shop."],
            });
        }

        var existing = await db.ProductModifierGroups
            .Where(link => link.ProductId == productId)
            .ToListAsync(cancellationToken);

        db.ProductModifierGroups.RemoveRange(existing);

        // The order the client sent is the order the sheet asks in — a replacement rather than a
        // merge, because "these are the questions, in this order" is one decision.
        for (var index = 0; index < wanted.Count; index++)
        {
            db.ProductModifierGroups.Add(new ProductModifierGroup
            {
                ProductId = productId,
                ModifierGroupId = wanted[index],
                SortOrder = index,
            });
        }

        await db.SaveChangesAsync(cancellationToken);

        return await ForProductAsync(productId, db, cancellationToken) switch
        {
            Results<Ok<IReadOnlyList<ModifierGroupResponse>>, NotFound> result
                when result.Result is Ok<IReadOnlyList<ModifierGroupResponse>> ok => ok,
            _ => TypedResults.NotFound(),
        };
    }

    /// <summary>
    /// Replaces a group's options wholesale.
    /// </summary>
    /// <remarks>
    /// A replacement rather than an add/remove pair, because the sheet's contents are one
    /// decision a person makes at once — and because a partial update would need a way to say
    /// "remove this one", which is a second endpoint for something nobody asked for.
    /// </remarks>
    private static async Task ReplaceOptionsAsync(
        AppDbContext db,
        Guid groupId,
        IReadOnlyList<Guid> productIds,
        CancellationToken cancellationToken)
    {
        var existing = await db.ModifierOptions
            .Where(o => o.ModifierGroupId == groupId)
            .ToListAsync(cancellationToken);

        db.ModifierOptions.RemoveRange(existing);

        for (var index = 0; index < productIds.Count; index++)
        {
            db.ModifierOptions.Add(new ModifierOption
            {
                ModifierGroupId = groupId,
                ProductId = productIds[index],
                SortOrder = index,
            });
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task<(string? Name, Dictionary<string, string[]> Errors)> ValidateAsync(
        SaveModifierGroupRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > ModifierGroup.NameMaxLength)
        {
            errors["name"] = [$"A name of 1 to {ModifierGroup.NameMaxLength} characters is required."];
        }

        var min = request.MinSelections ?? 0;

        if (min < 0)
        {
            errors["minSelections"] = ["A minimum of 0 or more is required."];
        }

        if (request.MaxSelections is { } max && max < min)
        {
            // A maximum below the minimum makes a group nothing can satisfy, which reads at the
            // till as an item that cannot be ordered with no message saying why.
            errors["maxSelections"] = ["A maximum below the minimum makes the question unanswerable."];
        }

        if (request.OptionProductIds is { Count: > 0 } options)
        {
            var found = await db.Products
                .Where(p => options.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(cancellationToken);

            if (found.Count != options.Distinct().Count())
            {
                errors["optionProductIds"] = ["One or more of those products does not exist in this shop."];
            }
        }

        return (name, errors);
    }

    private static async Task<IReadOnlyList<ModifierGroupResponse>> ProjectAsync(
        AppDbContext db,
        IReadOnlyList<ModifierGroup> groups,
        CancellationToken cancellationToken)
    {
        var ids = groups.Select(g => g.Id).ToArray();

        // One read joined to the product, because the option's price *is* the product's — there
        // is no second price to keep in step, which is the whole reason an option holds none.
        var options = await (
            from option in db.ModifierOptions.AsNoTracking()
            join product in db.Products on option.ProductId equals product.Id
            where ids.Contains(option.ModifierGroupId)
            orderby option.SortOrder
            select new
            {
                option.Id,
                option.ModifierGroupId,
                option.ProductId,
                product.Name,
                product.UnitPrice,
                option.SortOrder,
                option.IsDefault,
            }).ToListAsync(cancellationToken);

        return
        [
            .. groups.Select(g => new ModifierGroupResponse(
                g.Id,
                g.Name,
                g.MinSelections,
                g.MaxSelections,
                g.SortOrder,
                [
                    .. options
                        .Where(o => o.ModifierGroupId == g.Id)
                        .Select(o => new ModifierOptionResponse(
                            o.Id,
                            o.ProductId,
                            o.Name,
                            (decimal)o.UnitPrice,
                            o.SortOrder,
                            o.IsDefault)),
                ])),
        ];
    }
}
