using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Orders;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Orders;

namespace Pos.Api.Endpoints;

/// <summary>One thing to cook, as the station's screen draws it.</summary>
/// <remarks>
/// <b><see cref="IsVoided"/> is the only field not on the ticket row.</b> It is read from the
/// order line's <i>current</i> status, and it is the whole of how a void reaches a kitchen: the
/// ticket itself is never edited — see <c>KitchenTicket</c> — so the screen strikes the item
/// through and leaves the record of what was sent intact. A chef who has already plated it needs
/// to see that it was cancelled, not to watch it vanish.
/// </remarks>
public sealed record KitchenTicketLineResponse(
    Guid Id,
    Guid OrderLineId,
    int LineNumber,
    string Description,
    decimal Quantity,
    int? SeatNumber,
    string? ModifierText,
    string? Note,
    bool IsVoided);

/// <summary>A ticket on a station's screen.</summary>
public sealed record KitchenTicketResponse(
    Guid Id,
    Guid OrderId,
    Guid StationId,
    string StationName,
    int Course,
    long OrderNumber,
    string OrderLabel,
    DateTimeOffset FiredAt,
    KitchenTicketStatus Status,
    DateTimeOffset? BumpedAt,
    IReadOnlyList<KitchenTicketLineResponse> Lines);

/// <summary>
/// The kitchen display: what each station has been told to cook, and clearing it.
/// </summary>
/// <remarks>
/// <b>The display polls.</b> There is no SSE and no websocket anywhere in this project, and adding
/// a transport is its own phase with its own reconnection, authentication and proxy problems — so
/// the interval is <see cref="PollIntervalSeconds"/>, a stated and tunable number rather than an
/// oversight. A kitchen screen refreshing every few seconds is indistinguishable from a live one
/// at the speed food is cooked.
/// <para>
/// Everything here is <c>CanWorkKitchen</c>, which is everyone: the people bumping tickets are the
/// people cooking, and a display that needed a manager would be left logged in as one all night.
/// </para>
/// <para>
/// <b>Bump and recall carry no <c>Idempotency-Key</c>.</b> They move no money and no stock, and
/// they are idempotent by state rather than by a stored response — bumping a bumped ticket is
/// already a no-op. Requiring a key on an action a chef performs forty times an hour would be
/// friction that buys nothing, and invariant 6 is a rule about writes that move money.
/// </para>
/// </remarks>
public static class KitchenEndpoints
{
    /// <summary>
    /// How often a display should ask again, in seconds.
    /// </summary>
    /// <remarks>
    /// Served from here rather than hard-coded in the client so it can be changed for a shop with
    /// a slow line without shipping a new build. Five seconds is the number this phase chose: a
    /// plate takes minutes, and a poll per screen per five seconds is nothing next to the reads a
    /// register already makes.
    /// </remarks>
    public const int PollIntervalSeconds = 5;

    public static IEndpointRouteBuilder MapKitchenEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var kitchen = builder.MapGroup("/api/v1/kitchen")
            .WithTags("Kitchen")
            .RequireRestaurantMode();

        kitchen.MapGet("/tickets", ListAsync)
            .RequireAuthorization(Policies.CanWorkKitchen)
            .WithSummary("What a station has been told to cook");

        kitchen.MapPost("/tickets/{id:guid}/bump", BumpAsync)
            .RequireAuthorization(Policies.CanWorkKitchen)
            .WithSummary("Clear a ticket off the screen");

        kitchen.MapPost("/tickets/{id:guid}/recall", RecallAsync)
            .RequireAuthorization(Policies.CanWorkKitchen)
            .WithSummary("Put a bumped ticket back on the screen");

        return builder;
    }

    /// <summary>One station's queue, oldest first.</summary>
    /// <remarks>
    /// Oldest first, and that is the ordering the kitchen works in — a screen sorted by anything
    /// else is a screen where the table that has waited longest scrolls off the bottom.
    /// <para>
    /// Bumped tickets are excluded by default and available on request, so a chef can find the
    /// one they cleared by accident without a second endpoint.
    /// </para>
    /// </remarks>
    private static async Task<Ok<IReadOnlyList<KitchenTicketResponse>>> ListAsync(
        AppDbContext db,
        Guid? stationId,
        bool? includeBumped,
        CancellationToken cancellationToken)
    {
        var wantsBumped = includeBumped ?? false;

        var tickets = await db.KitchenTickets
            .AsNoTracking()
            .Where(t => (stationId == null || t.StationId == stationId)
                && (wantsBumped || t.Status == KitchenTicketStatus.Active))
            .OrderBy(t => t.FiredAt)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(await ProjectAsync(db, tickets, cancellationToken));
    }

    /// <summary>
    /// Marks a ticket cooked and takes it off the screen.
    /// </summary>
    /// <remarks>
    /// Bumping an already-bumped ticket answers 200 with the ticket unchanged rather than a
    /// conflict. Two chefs reaching for one screen is ordinary, and a 409 would say a mistake had
    /// been made when the outcome is exactly what both of them wanted.
    /// </remarks>
    private static async Task<Results<Ok<KitchenTicketResponse>, NotFound>> BumpAsync(
        Guid id,
        AppDbContext db,
        ICurrentActor actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(actor);

        // Scoped by the query filter, so another shop's ticket is a 404 indistinguishable from
        // one that does not exist.
        var ticket = await db.KitchenTickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        if (ticket.Status != KitchenTicketStatus.Bumped)
        {
            ticket.Status = KitchenTicketStatus.Bumped;
            ticket.BumpedAt = timeProvider.GetUtcNow();
            ticket.BumpedBy = actor.UserId;

            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ProjectOneAsync(db, ticket, cancellationToken));
    }

    /// <summary>
    /// Puts a bumped ticket back.
    /// </summary>
    /// <remarks>
    /// <b>A recall clears who bumped it and when</b>, rather than keeping a history of bumps. The
    /// row records the state a screen renders, and a ticket that was bumped and recalled is, for
    /// every purpose this phase has, a ticket that is up — the check constraint says a ticket is
    /// either cleared with a name and a time against it or it is not cleared at all.
    /// </remarks>
    private static async Task<Results<Ok<KitchenTicketResponse>, NotFound>> RecallAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var ticket = await db.KitchenTickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (ticket is null)
        {
            return TypedResults.NotFound();
        }

        if (ticket.Status != KitchenTicketStatus.Active)
        {
            ticket.Status = KitchenTicketStatus.Active;
            ticket.BumpedAt = null;
            ticket.BumpedBy = null;

            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await ProjectOneAsync(db, ticket, cancellationToken));
    }

    private static async Task<KitchenTicketResponse> ProjectOneAsync(
        AppDbContext db,
        KitchenTicket ticket,
        CancellationToken cancellationToken) =>
        (await ProjectAsync(db, [ticket], cancellationToken))[0];

    /// <summary>
    /// Loads the lines and the station names for a page of tickets.
    /// </summary>
    /// <remarks>
    /// Three queries for any number of tickets rather than three per ticket, because this is the
    /// read every screen in the kitchen makes every few seconds. The voided set is the join that
    /// earns its place: it is the only thing on a ticket that is allowed to change after firing.
    /// </remarks>
    internal static async Task<IReadOnlyList<KitchenTicketResponse>> ProjectAsync(
        AppDbContext db,
        IReadOnlyList<KitchenTicket> tickets,
        CancellationToken cancellationToken)
    {
        if (tickets.Count == 0)
        {
            return [];
        }

        var ticketIds = tickets.Select(t => t.Id).ToList();

        var lines = await db.KitchenTicketLines
            .AsNoTracking()
            .Where(l => ticketIds.Contains(l.KitchenTicketId))
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

        var stationNames = await db.Stations
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var orderLineIds = lines.Select(l => l.OrderLineId).Distinct().ToList();

        var voided = await db.OrderLines
            .AsNoTracking()
            .Where(l => orderLineIds.Contains(l.Id) && l.Status == OrderLineStatus.Voided)
            .Select(l => l.Id)
            .ToListAsync(cancellationToken);

        var voidedSet = voided.ToHashSet();

        return
        [
            .. tickets.Select(t => new KitchenTicketResponse(
                t.Id,
                t.OrderId,
                t.StationId,
                stationNames.TryGetValue(t.StationId, out var name) ? name : string.Empty,
                t.Course,
                t.OrderNumber,
                t.OrderLabel,
                t.FiredAt,
                t.Status,
                t.BumpedAt,
                [
                    .. lines
                        .Where(l => l.KitchenTicketId == t.Id)
                        .Select(l => new KitchenTicketLineResponse(
                            l.Id,
                            l.OrderLineId,
                            l.LineNumber,
                            l.Description,
                            l.Quantity,
                            l.SeatNumber,
                            l.ModifierText,
                            l.Note,
                            voidedSet.Contains(l.OrderLineId))),
                ])),
        ];
    }

    /// <summary>Projects tickets that have just been fired, so nothing on them is voided yet.</summary>
    /// <remarks>
    /// Separate from <see cref="ProjectAsync"/> and deliberately not a query: it runs inside the
    /// firing transaction, where the rows it would read are the ones being written.
    /// </remarks>
    internal static IReadOnlyList<KitchenTicketResponse> ProjectFired(
        IReadOnlyList<FiredTicket> fired,
        IReadOnlyDictionary<Guid, string> stationNames) =>
        [
            .. fired.Select(f => new KitchenTicketResponse(
                f.Ticket.Id,
                f.Ticket.OrderId,
                f.Ticket.StationId,
                stationNames.TryGetValue(f.Ticket.StationId, out var name) ? name : string.Empty,
                f.Ticket.Course,
                f.Ticket.OrderNumber,
                f.Ticket.OrderLabel,
                f.Ticket.FiredAt,
                f.Ticket.Status,
                f.Ticket.BumpedAt,
                [
                    .. f.Lines.Select(l => new KitchenTicketLineResponse(
                        l.Id,
                        l.OrderLineId,
                        l.LineNumber,
                        l.Description,
                        l.Quantity,
                        l.SeatNumber,
                        l.ModifierText,
                        l.Note,
                        IsVoided: false)),
                ])),
        ];
}
