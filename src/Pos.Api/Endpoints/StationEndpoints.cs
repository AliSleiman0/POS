using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Errors;
using Pos.Api.Orders;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>A station, as the kitchen and the menu editor see it.</summary>
public sealed record StationResponse(Guid Id, string Name, int SortOrder, bool IsActive);

/// <summary>Create or rename a station.</summary>
public sealed record SaveStationRequest(string? Name, int? SortOrder, bool? IsActive);

/// <summary>
/// The stations a kitchen is divided into.
/// </summary>
/// <remarks>
/// <b>Reads are <c>CanWorkKitchen</c> and writes are <c>CanManageFloor</c>.</b> Everyone cooking
/// has to be able to pick which screen they are standing at; changing what the stations <i>are</i>
/// re-routes the menu, which is the same authority as renaming a table.
/// <para>
/// There is no delete. A station is retired through <see cref="Station.IsActive"/>, because
/// tickets point at the one they were sent to and stay readable for as long as the order does.
/// The list is short enough that a shop never needs one to disappear.
/// </para>
/// </remarks>
public static class StationEndpoints
{
    private const string DuplicateStationNameConstraint = "ux_station_tenant_name";

    public static IEndpointRouteBuilder MapStationEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var stations = builder.MapGroup("/api/v1/stations")
            .WithTags("Stations")
            .RequireRestaurantMode();

        stations.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanWorkKitchen)
            .WithSummary("The kitchen's stations");

        stations.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanManageFloor)
            .WithSummary("Add a station");

        stations.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Policies.CanManageFloor)
            .WithSummary("Rename, reorder or retire a station");

        return builder;
    }

    private static async Task<Ok<IReadOnlyList<StationResponse>>> ListAsync(
        AppDbContext db,
        bool? includeRetired,
        CancellationToken cancellationToken)
    {
        var wanted = includeRetired ?? false;

        var stations = await db.Stations
            .AsNoTracking()
            .Where(s => wanted || s.IsActive)
            .OrderBy(s => s.SortOrder).ThenBy(s => s.Name)
            .ToListAsync(cancellationToken);

        IReadOnlyList<StationResponse> response = [.. stations.Select(Project)];

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Created<StationResponse>, ValidationProblem>> CreateAsync(
        SaveStationRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > Station.NameMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"A name of 1 to {Station.NameMaxLength} characters is required."],
            });
        }

        var station = new Station
        {
            Name = name,
            SortOrder = request.SortOrder ?? 0,
            IsActive = request.IsActive ?? true,
        };

        db.Stations.Add(station);

        if (await SaveOrDuplicateNameAsync(db, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return TypedResults.Created($"/api/v1/stations/{station.Id}", Project(station));
    }

    private static async Task<Results<Ok<StationResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        SaveStationRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = request.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > Station.NameMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"A name of 1 to {Station.NameMaxLength} characters is required."],
            });
        }

        var station = await db.Stations.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (station is null)
        {
            return TypedResults.NotFound();
        }

        station.Name = name;
        station.SortOrder = request.SortOrder ?? station.SortOrder;
        station.IsActive = request.IsActive ?? station.IsActive;

        if (await SaveOrDuplicateNameAsync(db, cancellationToken) is { } duplicate)
        {
            return duplicate;
        }

        return TypedResults.Ok(Project(station));
    }

    /// <summary>
    /// Saves, turning a duplicate station name into a field error rather than a 500.
    /// </summary>
    /// <remarks>
    /// The unique index is the authority, not a pre-check — the same race
    /// <c>FloorEndpoints</c> documents for a table name. What this adds is the message.
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
            when (PostgresErrors.IsUniqueViolation(exception, DuplicateStationNameConstraint))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = ["Another station already has that name."],
            });
        }
    }

    private static StationResponse Project(Station station) =>
        new(station.Id, station.Name, station.SortOrder, station.IsActive);
}
