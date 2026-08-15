using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Errors;
using Pos.Api.Orders;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>A table, as the floor screen draws it.</summary>
public sealed record DiningTableResponse(
    Guid Id,
    Guid ServiceAreaId,
    string Name,
    int Seats,
    int SortOrder,
    bool IsActive);

/// <summary>An area and the tables in it.</summary>
public sealed record ServiceAreaResponse(
    Guid Id,
    string Name,
    int SortOrder,
    bool IsActive,
    IReadOnlyList<DiningTableResponse> Tables);

/// <summary>Create or rename an area.</summary>
public sealed record SaveServiceAreaRequest(string? Name, int? SortOrder, bool? IsActive);

/// <summary>Create or rename a table.</summary>
public sealed record SaveDiningTableRequest(
    Guid? ServiceAreaId,
    string? Name,
    int? Seats,
    int? SortOrder,
    bool? IsActive);

/// <summary>
/// The room: areas and the tables in them.
/// </summary>
/// <remarks>
/// <b>A list, not a floor plan.</b> There are no coordinates and there is no canvas editor —
/// that is a stated non-goal of Phase 10 rather than an omission. What staff need to find a table
/// is its name and the area it is in, and both are here.
/// <para>
/// Reads are <c>CanTakeOrders</c> and writes are <c>CanManageFloor</c>: everybody working the room
/// has to see it, and renaming a table changes what every historical report about it says.
/// </para>
/// </remarks>
public static class FloorEndpoints
{
    private const string DuplicateTableNameConstraint = "ux_dining_table_tenant_name";

    public static IEndpointRouteBuilder MapFloorEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var floor = builder.MapGroup("/api/v1/floor")
            .WithTags("Floor")
            .RequireRestaurantMode();

        floor.MapGet("/", ReadAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("The room: areas and their tables");

        floor.MapPost("/areas", CreateAreaAsync)
            .RequireAuthorization(Policies.CanManageFloor)
            .WithSummary("Add an area to the room");

        floor.MapPut("/areas/{id:guid}", UpdateAreaAsync)
            .RequireAuthorization(Policies.CanManageFloor)
            .WithSummary("Rename or retire an area");

        floor.MapPost("/tables", CreateTableAsync)
            .RequireAuthorization(Policies.CanManageFloor)
            .WithSummary("Add a table");

        floor.MapPut("/tables/{id:guid}", UpdateTableAsync)
            .RequireAuthorization(Policies.CanManageFloor)
            .WithSummary("Rename, reseat or retire a table");

        return builder;
    }

    private static async Task<Ok<IReadOnlyList<ServiceAreaResponse>>> ReadAsync(
        AppDbContext db,
        bool? includeRetired,
        CancellationToken cancellationToken)
    {
        // Retired areas and tables are excluded by default and available on request. They are
        // never deleted — orders reference the table they were served at, and reports read back
        // through that reference for months.
        var wanted = includeRetired ?? false;

        var areas = await db.ServiceAreas
            .AsNoTracking()
            .Where(a => wanted || a.IsActive)
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Name)
            .ToListAsync(cancellationToken);

        var tables = await db.DiningTables
            .AsNoTracking()
            .Where(t => wanted || t.IsActive)
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .ToListAsync(cancellationToken);

        IReadOnlyList<ServiceAreaResponse> response =
        [
            .. areas.Select(a => new ServiceAreaResponse(
                a.Id,
                a.Name,
                a.SortOrder,
                a.IsActive,
                [
                    .. tables
                        .Where(t => t.ServiceAreaId == a.Id)
                        .Select(t => Project(t)),
                ])),
        ];

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Created<ServiceAreaResponse>, ValidationProblem>> CreateAreaAsync(
        SaveServiceAreaRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > ServiceArea.NameMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"A name of 1 to {ServiceArea.NameMaxLength} characters is required."],
            });
        }

        var area = new ServiceArea
        {
            Name = name,
            SortOrder = request.SortOrder ?? 0,
            IsActive = request.IsActive ?? true,
        };

        db.ServiceAreas.Add(area);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/v1/floor/areas/{area.Id}",
            new ServiceAreaResponse(area.Id, area.Name, area.SortOrder, area.IsActive, []));
    }

    private static async Task<Results<Ok<ServiceAreaResponse>, NotFound, ValidationProblem>> UpdateAreaAsync(
        Guid id,
        SaveServiceAreaRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > ServiceArea.NameMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"A name of 1 to {ServiceArea.NameMaxLength} characters is required."],
            });
        }

        // Scoped by the query filter, so another tenant's area is a 404 indistinguishable from
        // one that does not exist.
        var area = await db.ServiceAreas.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (area is null)
        {
            return TypedResults.NotFound();
        }

        area.Name = name;
        area.SortOrder = request.SortOrder ?? area.SortOrder;
        area.IsActive = request.IsActive ?? area.IsActive;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new ServiceAreaResponse(area.Id, area.Name, area.SortOrder, area.IsActive, []));
    }

    private static async Task<Results<Created<DiningTableResponse>, ValidationProblem>> CreateTableAsync(
        SaveDiningTableRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, errors) = await ValidateTableAsync(request, db, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var table = new DiningTable
        {
            ServiceAreaId = request.ServiceAreaId!.Value,
            Name = name!,
            Seats = request.Seats ?? 0,
            SortOrder = request.SortOrder ?? 0,
            IsActive = request.IsActive ?? true,
        };

        db.DiningTables.Add(table);

        if (await SaveOrDuplicateNameAsync(db, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return TypedResults.Created($"/api/v1/floor/tables/{table.Id}", Project(table));
    }

    private static async Task<Results<Ok<DiningTableResponse>, NotFound, ValidationProblem>> UpdateTableAsync(
        Guid id,
        SaveDiningTableRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (name, errors) = await ValidateTableAsync(request, db, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var table = await db.DiningTables.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (table is null)
        {
            return TypedResults.NotFound();
        }

        table.ServiceAreaId = request.ServiceAreaId!.Value;
        table.Name = name!;
        table.Seats = request.Seats ?? table.Seats;
        table.SortOrder = request.SortOrder ?? table.SortOrder;
        table.IsActive = request.IsActive ?? table.IsActive;

        if (await SaveOrDuplicateNameAsync(db, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return TypedResults.Ok(Project(table));
    }

    private static async Task<(string? Name, Dictionary<string, string[]> Errors)> ValidateTableAsync(
        SaveDiningTableRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > DiningTable.NameMaxLength)
        {
            errors["name"] = [$"A name of 1 to {DiningTable.NameMaxLength} characters is required."];
        }

        if (request.ServiceAreaId is not { } areaId || areaId == Guid.Empty)
        {
            errors["serviceAreaId"] = ["An area is required."];
        }
        else if (!await db.ServiceAreas.AnyAsync(a => a.Id == areaId, cancellationToken))
        {
            // 400 on the field rather than 404, and identical whether the area is unknown or
            // another tenant's — so it is not an existence oracle. Same shape as a shift's
            // registerId.
            errors["serviceAreaId"] = ["No area with that id exists in this shop."];
        }

        if (request.Seats is { } seats && seats < 0)
        {
            errors["seats"] = ["A seat count of 0 or more is required."];
        }

        return (name, errors);
    }

    /// <summary>
    /// Saves, turning a duplicate table name into a field error rather than a 500.
    /// </summary>
    /// <remarks>
    /// The unique index is the authority, not a pre-check — two people adding "Table 4" at the
    /// same moment both pass "does that name exist yet?" and both insert, which is the same race
    /// <c>PostgresErrors</c> exists for. What this adds is the message: a manager building a
    /// floor plan needs to be told the name is taken, not shown an unexplained failure.
    /// </remarks>
    private static async Task<ValidationProblem?> SaveOrDuplicateNameAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateException exception)
            when (PostgresErrors.IsUniqueViolation(exception, DuplicateTableNameConstraint))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Another table already has that name."],
            });
        }
    }

    private static DiningTableResponse Project(DiningTable table) =>
        new(table.Id, table.ServiceAreaId, table.Name, table.Seats, table.SortOrder, table.IsActive);
}
