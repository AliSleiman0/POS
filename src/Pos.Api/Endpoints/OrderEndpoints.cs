using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Errors;
using Pos.Api.Idempotency;
using Pos.Api.Orders;
using Pos.Core.Auditing;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Menus;
using Pos.Core.Monetary;
using Pos.Data;
using Pos.Data.Orders;

namespace Pos.Api.Endpoints;

/// <summary>Seat a table, start a tab, or take a takeaway.</summary>
/// <remarks>
/// <b>There is no <c>openedBy</c> and no <c>orderNumber</c>.</b> Both are the server's to decide —
/// the first from the validated token, the second from the per-tenant counter inside the opening
/// transaction. A caller able to supply either could attribute a table to a colleague or collide
/// two orders on one number.
/// </remarks>
public sealed record OpenOrderRequest(
    OrderType? Type,
    Guid? DiningTableId,
    string? TabName,
    int? CoverCount,
    string? Note);

/// <summary>One item to add, with its modifiers.</summary>
/// <remarks>
/// <c>UnitPriceOverride</c> and <c>DiscountAmount</c> are gated exactly as they are on a sale: a
/// caller without the policy is refused rather than quietly ignored, because ignoring one changes
/// what the customer pays.
/// </remarks>
public sealed record AddOrderLineRequest(
    Guid? ProductId,
    decimal? Quantity,
    int? Course,
    int? SeatNumber,
    string? Note,
    decimal? UnitPriceOverride,
    decimal? DiscountAmount,
    IReadOnlyList<AddOrderLineRequest>? Modifiers);

/// <summary>The lines to put on an order.</summary>
public sealed record AddOrderLinesRequest(IReadOnlyList<AddOrderLineRequest>? Lines);

/// <summary>What may be changed about a line that has not been sent to the kitchen.</summary>
public sealed record AmendOrderLineRequest(
    decimal? Quantity,
    int? Course,
    int? SeatNumber,
    string? Note);

/// <summary>Why a line is coming off.</summary>
public sealed record VoidOrderLineRequest(string? Reason);

/// <summary>Which round to send to the kitchen.</summary>
/// <remarks>
/// <c>Course</c> omitted means everything still pending, which is what a takeaway and a bar tab
/// want — they have one round and being made to name it would be ceremony. A dining room names
/// the course, because that is the entire point of coursing.
/// </remarks>
public sealed record FireOrderRequest(int? Course);

/// <summary>Where an order is moving to.</summary>
public sealed record TransferOrderRequest(Guid? DiningTableId, string? TabName);

/// <summary>Which order is being absorbed into this one.</summary>
public sealed record MergeOrderRequest(Guid? SourceOrderId);

/// <summary>Why an order is being written off.</summary>
public sealed record AbandonOrderRequest(string? Reason);

/// <summary>One line of an order, as the screen and the kitchen see it.</summary>
/// <remarks>
/// <b>It carries no line total.</b> The amounts belong to a bill and come from the pricing
/// engine when one is quoted or settled — see <c>OrderLine</c>. A total computed here as well
/// would be a second number to keep in step, and the first time it drifted the screen would show
/// something the sale would not charge.
/// </remarks>
public sealed record OrderLineResponse(
    Guid Id,
    int LineNumber,
    Guid ProductId,
    Guid? ParentOrderLineId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal DiscountAmount,
    int Course,
    int? SeatNumber,
    string? Note,
    OrderLineStatus Status,
    DateTimeOffset? FiredAt);

/// <summary>An order, with its lines.</summary>
public sealed record OrderResponse(
    Guid Id,
    long OrderNumber,
    OrderType Type,
    OrderStatus Status,
    Guid? DiningTableId,
    string? TableName,
    string? TabName,
    int? CoverCount,
    string? Note,
    Guid OpenedBy,
    DateTimeOffset OpenedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<OrderLineResponse> Lines);

/// <summary>
/// Orders: the mutable working state a restaurant serves from.
/// </summary>
/// <remarks>
/// <b>Nothing here moves money.</b> An order is not a financial record — it is edited freely for
/// an hour and it may end up abandoned. The money happens once, when a bill is settled through
/// <c>ISaleWriter</c> in 10.5, and that is the only path a <c>Sale</c> is written by.
/// <para>
/// The whole group carries <c>RequireRestaurantMode()</c>, so a retail shop gets
/// <c>409 restaurant-mode-required</c> from every route rather than a 404 that would send a
/// client hunting for a typo.
/// </para>
/// </remarks>
public static class OrderEndpoints
{
    private const string OpenTableConstraint = "ux_customer_order_tenant_table_open";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var orders = builder.MapGroup("/api/v1/orders")
            .WithTags("Orders")
            .RequireRestaurantMode();

        orders.MapPost("/", OpenAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .RequireIdempotency()
            .WithSummary("Seat a table, start a tab or take a takeaway");

        orders.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("Open orders, oldest first");

        orders.MapGet("/{id:guid}", ReadAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("One order and its lines");

        orders.MapPost("/{id:guid}/lines", AddLinesAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .RequireIdempotency()
            .WithSummary("Put items on an order");

        orders.MapPatch("/{id:guid}/lines/{lineId:guid}", AmendLineAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("Change a line the kitchen has not been told about");

        // CanTakeOrders at the route, and CanVoidFiredLine re-checked in the handler once the
        // line's status is known. A pending line costs the shop nothing to remove; a fired one
        // is food already cooking, which is the restaurant's CanVoidSale.
        orders.MapPost("/{id:guid}/lines/{lineId:guid}/void", VoidLineAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("Take a line off an order");

        // CanTakeOrders: sending a round to the kitchen is the ordinary business of serving a
        // table. What costs the shop something is taking it back off, and that is the void.
        orders.MapPost("/{id:guid}/fire", FireAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .RequireIdempotency()
            .WithSummary("Send a round to the kitchen");

        orders.MapPost("/{id:guid}/transfer", TransferAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .RequireIdempotency()
            .WithSummary("Move an order to another table");

        orders.MapPost("/{id:guid}/merge", MergeAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .RequireIdempotency()
            .WithSummary("Absorb another order into this one");

        // CanVoidFiredLine, not CanTakeOrders: abandoning writes off whatever was cooked, which
        // is the same authority as voiding a fired line and for the same reason.
        orders.MapPost("/{id:guid}/abandon", AbandonAsync)
            .RequireAuthorization(Policies.CanVoidFiredLine)
            .RequireIdempotency()
            .WithSummary("Write off an order nobody paid for");

        return builder;
    }

    private static async Task<Results<Created<OrderResponse>, ValidationProblem>> OpenAsync(
        OpenOrderRequest request,
        AppDbContext db,
        OrderWriter writer,
        IIdempotencyContext idempotency,
        ICurrentActor actor,
        IAuditLog audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.Type is not { } type || !Enum.IsDefined(type))
        {
            errors["type"] = [$"One of {string.Join(", ", Enum.GetNames<OrderType>())} is required."];
            return TypedResults.ValidationProblem(errors);
        }

        var tabName = request.TabName?.Trim();

        switch (type)
        {
            case OrderType.Table:
                if (request.DiningTableId is not { } tableId || tableId == Guid.Empty)
                {
                    errors["diningTableId"] = ["A table is required."];
                }
                else if (!await db.DiningTables.AnyAsync(t => t.Id == tableId && t.IsActive, cancellationToken))
                {
                    // 400 on the field rather than 404, identical whether the table is unknown,
                    // retired or another shop's — so it is not an existence oracle.
                    errors["diningTableId"] = ["No active table with that id exists in this shop."];
                }

                break;

            case OrderType.Tab:
                if (string.IsNullOrEmpty(tabName))
                {
                    // Required, because a tab with no name cannot be found again — which is the
                    // entire purpose of a tab.
                    errors["tabName"] = ["A name is required so the tab can be found again."];
                }
                else if (tabName.Length > Order.TabNameMaxLength)
                {
                    errors["tabName"] = [$"At most {Order.TabNameMaxLength} characters."];
                }

                break;

            case OrderType.Takeaway:
                break;

            default:
                break;
        }

        if (type != OrderType.Table && request.DiningTableId is not null)
        {
            errors["diningTableId"] = ["Only a table order is seated at a table."];
        }

        if (request.CoverCount is { } covers && covers <= 0)
        {
            errors["coverCount"] = ["A cover count of 1 or more, or none at all."];
        }

        var note = request.Note?.Trim();

        if (note is { Length: > Order.NoteMaxLength })
        {
            errors["note"] = [$"At most {Order.NoteMaxLength} characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var openedAt = timeProvider.GetUtcNow();

        Order order;

        try
        {
            order = await writer.OpenAsync(
                new OrderOpenRequest(
                    type,
                    type == OrderType.Table ? request.DiningTableId : null,
                    type == OrderType.Tab ? tabName : null,
                    actor.RegisterId,
                    request.CoverCount,
                    string.IsNullOrEmpty(note) ? null : note),
                opened =>
                {
                    idempotency.Record(db, StatusCodes.Status201Created, Project(opened, null, []), openedAt);

                    audit.Record(
                        AuditAction.OrderOpened,
                        nameof(Order),
                        opened.Id,
                        after: new Dictionary<string, string?>
                        {
                            ["orderNumber"] = opened.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                            ["type"] = opened.Type.ToString(),
                            ["table"] = opened.DiningTableId?.ToString(),
                            ["tabName"] = opened.TabName,
                        });
                },
                cancellationToken);
        }
        catch (DbUpdateException exception)
            when (PostgresErrors.IsUniqueViolation(exception, OpenTableConstraint))
        {
            // The filtered unique index is the authority, not a pre-check: two staff seating one
            // table in the same second both pass "is anything open here?" and both insert.
            throw new TableAlreadyOccupiedException();
        }

        var tableName = await TableNameAsync(db, order.DiningTableId, cancellationToken);

        return TypedResults.Created($"/api/v1/orders/{order.Id}", Project(order, tableName, []));
    }

    /// <summary>Open orders, oldest first — the floor screen's read.</summary>
    /// <remarks>
    /// Lines are deliberately not included. A busy room is thirty orders of a dozen lines each,
    /// and the floor screen shows a card per table; the detail read is one tap away and is what
    /// <c>GET /orders/{id}</c> is for.
    /// </remarks>
    private static async Task<Ok<IReadOnlyList<OrderResponse>>> ListAsync(
        AppDbContext db,
        OrderStatus? status,
        CancellationToken cancellationToken)
    {
        var wanted = status ?? OrderStatus.Open;

        var orders = await db.Orders
            .AsNoTracking()
            .Where(o => o.Status == wanted)
            .OrderBy(o => o.OpenedAt)
            .ToListAsync(cancellationToken);

        var tableNames = await TableNamesAsync(db, cancellationToken);

        IReadOnlyList<OrderResponse> response =
        [
            .. orders.Select(o => Project(
                o,
                o.DiningTableId is { } id && tableNames.TryGetValue(id, out var name) ? name : null,
                [])),
        ];

        return TypedResults.Ok(response);
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound>> ReadAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // Scoped by the query filter, so another tenant's order is a 404 indistinguishable from
        // one that does not exist.
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return TypedResults.NotFound();
        }

        var lines = await LinesAsync(db, id, cancellationToken);
        var tableName = await TableNameAsync(db, order.DiningTableId, cancellationToken);

        return TypedResults.Ok(Project(order, tableName, lines));
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound, ValidationProblem, ProblemHttpResult>>
        AddLinesAsync(
            Guid id,
            AddOrderLinesRequest request,
            AppDbContext db,
            OrderWriter writer,
            IIdempotencyContext idempotency,
            ClaimsPrincipal caller,
            IAuthorizationService authorization,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (!await db.Orders.AnyAsync(o => o.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        if (request.Lines is not { Count: > 0 })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["lines"] = ["At least one line is required."],
            });
        }

        var (instructions, errors) = await ResolveAsync(db, request.Lines, "lines", cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // The questions the menu asks, checked at the API rather than only in the sheet. A
        // client that skipped a required group would otherwise send a steak to the grill with
        // no temperature on it, and the first anybody knew would be the chef shouting.
        await CheckModifiersAsync(db, instructions, "lines", errors, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Gated exactly as a sale's are, and refused rather than silently dropped: quietly
        // ignoring an override would charge the customer the shelf price after somebody told
        // them otherwise.
        var missing = await MissingAdjustmentPoliciesAsync(instructions, caller, authorization);

        if (missing.Count > 0)
        {
            return AdjustmentNotAuthorized(missing);
        }

        var recordedAt = timeProvider.GetUtcNow();

        await writer.AddLinesAsync(
            id,
            instructions,
            _ => idempotency.Record(db, StatusCodes.Status200OK, new { orderId = id }, recordedAt),
            cancellationToken);

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == id, cancellationToken);
        var lines = await LinesAsync(db, id, cancellationToken);
        var tableName = await TableNameAsync(db, order.DiningTableId, cancellationToken);

        return TypedResults.Ok(Project(order, tableName, lines));
    }

    /// <summary>
    /// Changes a line the kitchen has not been told about.
    /// </summary>
    /// <remarks>
    /// <b>Only while it is <see cref="OrderLineStatus.Pending"/>.</b> Once a line is fired the
    /// kitchen is cooking what it was told, and silently changing the quantity underneath would
    /// bill for three of something two of which exist. Changing a fired line means voiding it
    /// and ordering again, which is what actually happens in a kitchen.
    /// </remarks>
    private static async Task<Results<Ok<OrderLineResponse>, NotFound, ValidationProblem>> AmendLineAsync(
        Guid id,
        Guid lineId,
        AmendOrderLineRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var line = await db.OrderLines
            .FirstOrDefaultAsync(l => l.Id == lineId && l.OrderId == id, cancellationToken);

        if (line is null)
        {
            return TypedResults.NotFound();
        }

        if (line.Status != OrderLineStatus.Pending)
        {
            throw new OrderNotOpenException(
                "That item has already gone to the kitchen. Void it and order again.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (request.Quantity is { } quantity && (quantity <= 0m || !CatalogRules.IsStorableAmount(quantity)))
        {
            errors["quantity"] = ["A quantity greater than zero with at most 4 decimal places is required."];
        }

        if (request.Course is { } course && course < 1)
        {
            errors["course"] = ["Courses start at 1."];
        }

        if (request.SeatNumber is { } seat && seat < 1)
        {
            errors["seatNumber"] = ["Seats start at 1."];
        }

        var note = request.Note?.Trim();

        if (note is { Length: > OrderLine.NoteMaxLength })
        {
            errors["note"] = [$"At most {OrderLine.NoteMaxLength} characters."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        line.Quantity = request.Quantity ?? line.Quantity;
        line.Course = request.Course ?? line.Course;
        line.SeatNumber = request.SeatNumber ?? line.SeatNumber;
        line.Note = string.IsNullOrEmpty(note) ? line.Note : note;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(Project(line));
    }

    private static async Task<Results<Ok<OrderLineResponse>, NotFound, ValidationProblem, ProblemHttpResult>>
        VoidLineAsync(
            Guid id,
            Guid lineId,
            VoidOrderLineRequest request,
            AppDbContext db,
            IAuditLog audit,
            ICurrentActor actor,
            ClaimsPrincipal caller,
            IAuthorizationService authorization,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var line = await db.OrderLines
            .FirstOrDefaultAsync(l => l.Id == lineId && l.OrderId == id, cancellationToken);

        if (line is null)
        {
            return TypedResults.NotFound();
        }

        if (line.Status == OrderLineStatus.Voided)
        {
            // Already off the bill. A second void would file a second audit entry describing a
            // loss that happened once.
            return TypedResults.Ok(Project(line));
        }

        var reason = request.Reason?.Trim();

        if (line.Status == OrderLineStatus.Fired)
        {
            // Food that is already cooking. Two things follow, and both are the point of the
            // status existing: it takes a supervisor, and it takes a reason.
            if (!(await authorization.AuthorizeAsync(caller, Policies.CanVoidFiredLine)).Succeeded)
            {
                return AdjustmentNotAuthorized([Policies.CanVoidFiredLine]);
            }

            if (string.IsNullOrEmpty(reason))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["reason"] = ["A reason is required: this is food the shop has already cooked."],
                });
            }
        }

        if (reason is { Length: > OrderLine.VoidReasonMaxLength })
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = [$"At most {OrderLine.VoidReasonMaxLength} characters."],
            });
        }

        var wasFired = line.Status == OrderLineStatus.Fired;
        var voidedAt = timeProvider.GetUtcNow();

        // The row stays. A deleted line is a question nobody can answer later — "why did the
        // kitchen cook a steak that is on no bill?"
        line.Status = OrderLineStatus.Voided;
        line.VoidedAt = voidedAt;
        line.VoidedBy = actor.UserId;
        line.VoidReason = string.IsNullOrEmpty(reason) ? null : reason;

        // Modifiers travel with their parent: "no cheese" on a burger nobody is having is not a
        // thing the kitchen or the bill should still be carrying.
        var children = await db.OrderLines
            .Where(l => l.ParentOrderLineId == lineId && l.Status != OrderLineStatus.Voided)
            .ToListAsync(cancellationToken);

        foreach (var child in children)
        {
            child.Status = OrderLineStatus.Voided;
            child.VoidedAt = voidedAt;
            child.VoidedBy = actor.UserId;
            child.VoidReason = line.VoidReason;
        }

        if (wasFired)
        {
            // Audited only when it cost the shop something. A pending line coming off is a
            // customer changing their mind before anybody cooked, and filing an entry for every
            // one of those is how a log stops being read.
            audit.Record(
                AuditAction.OrderLineVoided,
                nameof(OrderLine),
                line.Id,
                before: new Dictionary<string, string?>
                {
                    ["description"] = line.Description,
                    ["quantity"] = line.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["status"] = nameof(OrderLineStatus.Fired),
                },
                after: new Dictionary<string, string?>
                {
                    ["status"] = nameof(OrderLineStatus.Voided),
                    ["reason"] = line.VoidReason,
                });
        }

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(Project(line));
    }

    /// <summary>
    /// Sends a round to the kitchen, one ticket per station.
    /// </summary>
    /// <remarks>
    /// <b>A second tap creates nothing and says so.</b> The writer acts on pending lines and marks
    /// them fired in the same transaction, so firing twice returns an empty list rather than
    /// cooking the round twice — and that holds for a double-tap from the second handheld, which
    /// is carrying a different idempotency key and would defeat a stored response. The key is
    /// still required, because a genuine network retry should replay the original tickets rather
    /// than answer "nothing to fire" and leave a waiter wondering.
    /// <para>
    /// <b>Nothing on it is audited.</b> Firing moves no money and no stock — see Phase 10's rule 4
    /// — and <c>AuditAction</c> is deliberately a short list of the actions that do. What is
    /// recorded is the void afterwards, which is where a plate leaves without being paid for.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<IReadOnlyList<KitchenTicketResponse>>, NotFound, ValidationProblem>>
        FireAsync(
            Guid id,
            FireOrderRequest request,
            AppDbContext db,
            KitchenTicketWriter writer,
            IIdempotencyContext idempotency,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (request.Course is { } course && course < 1)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["course"] = ["Courses start at 1."],
            });
        }

        // 404 before the writer, so an unknown order reads the same here as everywhere else. The
        // narrow race — settled between this and the lock — is the writer's OrderNotOpenException.
        if (!await db.Orders.AnyAsync(o => o.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        // Read before the transaction, because the stored idempotent response is composed inside
        // it and a kitchen has a handful of stations. Naming them on the response is what lets a
        // till say "away to the grill and the bar" without a second round trip.
        var stationNames = await db.Stations
            .AsNoTracking()
            .ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var firedAt = timeProvider.GetUtcNow();

        var tickets = await writer.FireAsync(
            id,
            request.Course,
            fired => idempotency.Record(
                db,
                StatusCodes.Status200OK,
                KitchenEndpoints.ProjectFired(fired, stationNames),
                firedAt),
            cancellationToken);

        return TypedResults.Ok(KitchenEndpoints.ProjectFired(tickets, stationNames));
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound, ValidationProblem>> TransferAsync(
        Guid id,
        TransferOrderRequest request,
        AppDbContext db,
        IIdempotencyContext idempotency,
        IAuditLog audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (order.Status != OrderStatus.Open)
        {
            throw new OrderNotOpenException();
        }

        var tabName = request.TabName?.Trim();

        if (request.DiningTableId is { } tableId && tableId != Guid.Empty)
        {
            if (!await db.DiningTables.AnyAsync(t => t.Id == tableId && t.IsActive, cancellationToken))
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["diningTableId"] = ["No active table with that id exists in this shop."],
                });
            }

            var before = order.DiningTableId;

            order.DiningTableId = tableId;
            order.TabName = null;
            order.Type = OrderType.Table;

            audit.Record(
                AuditAction.OrderTransferred,
                nameof(Order),
                order.Id,
                before: new Dictionary<string, string?> { ["table"] = before?.ToString() },
                after: new Dictionary<string, string?> { ["table"] = tableId.ToString() });
        }
        else if (!string.IsNullOrEmpty(tabName))
        {
            if (tabName.Length > Order.TabNameMaxLength)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["tabName"] = [$"At most {Order.TabNameMaxLength} characters."],
                });
            }

            var before = order.DiningTableId;

            order.DiningTableId = null;
            order.TabName = tabName;
            order.Type = OrderType.Tab;

            audit.Record(
                AuditAction.OrderTransferred,
                nameof(Order),
                order.Id,
                before: new Dictionary<string, string?> { ["table"] = before?.ToString() },
                after: new Dictionary<string, string?> { ["tabName"] = tabName });
        }
        else
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["diningTableId"] = ["A table or a tab name is required."],
            });
        }

        idempotency.Record(db, StatusCodes.Status200OK, new { orderId = id }, timeProvider.GetUtcNow());

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
            when (PostgresErrors.IsUniqueViolation(exception, OpenTableConstraint))
        {
            // The destination is already being served. The index catches it for the same reason
            // it catches a double seating, and the message is the one a waiter can act on.
            throw new TableAlreadyOccupiedException();
        }

        var lines = await LinesAsync(db, id, cancellationToken);
        var tableName = await TableNameAsync(db, order.DiningTableId, cancellationToken);

        return TypedResults.Ok(Project(order, tableName, lines));
    }

    /// <summary>
    /// Absorbs another order's lines into this one.
    /// </summary>
    /// <remarks>
    /// Two tables pushed together, or a tab that turns into a table. The source is
    /// <b>closed rather than abandoned</b> — nothing was written off, its items simply live on
    /// the destination now, and marking it abandoned would report a loss that did not happen.
    /// </remarks>
    private static async Task<Results<Ok<OrderResponse>, NotFound, ValidationProblem>> MergeAsync(
        Guid id,
        MergeOrderRequest request,
        AppDbContext db,
        IIdempotencyContext idempotency,
        IAuditLog audit,
        ICurrentActor actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var target = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (target is null)
        {
            return TypedResults.NotFound();
        }

        if (request.SourceOrderId is not { } sourceId || sourceId == Guid.Empty || sourceId == id)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sourceOrderId"] = ["A different open order is required."],
            });
        }

        var source = await db.Orders.FirstOrDefaultAsync(o => o.Id == sourceId, cancellationToken);

        if (source is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["sourceOrderId"] = ["No order with that id exists in this shop."],
            });
        }

        if (target.Status != OrderStatus.Open || source.Status != OrderStatus.Open)
        {
            throw new OrderNotOpenException();
        }

        var now = timeProvider.GetUtcNow();

        var moving = await db.OrderLines
            .Where(l => l.OrderId == sourceId)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

        var nextNumber = await db.OrderLines
            .Where(l => l.OrderId == id)
            .Select(l => (int?)l.LineNumber)
            .MaxAsync(cancellationToken) ?? 0;

        foreach (var line in moving)
        {
            line.OrderId = id;
            line.LineNumber = ++nextNumber;
        }

        source.Status = OrderStatus.Closed;
        source.ClosedAt = now;
        source.ClosedBy = actor.UserId;

        audit.Record(
            AuditAction.OrderMerged,
            nameof(Order),
            target.Id,
            after: new Dictionary<string, string?>
            {
                ["absorbed"] = source.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lines"] = moving.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

        idempotency.Record(db, StatusCodes.Status200OK, new { orderId = id }, now);

        await db.SaveChangesAsync(cancellationToken);

        var lines = await LinesAsync(db, id, cancellationToken);
        var tableName = await TableNameAsync(db, target.DiningTableId, cancellationToken);

        return TypedResults.Ok(Project(target, tableName, lines));
    }

    private static async Task<Results<Ok<OrderResponse>, NotFound, ValidationProblem>> AbandonAsync(
        Guid id,
        AbandonOrderRequest request,
        AppDbContext db,
        IIdempotencyContext idempotency,
        IAuditLog audit,
        ICurrentActor actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (order.Status != OrderStatus.Open)
        {
            throw new OrderNotOpenException();
        }

        var reason = request.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            // Required, like a void's and a stock adjustment's. This is the record somebody
            // wants six months later and will not have.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["A reason is required."],
            });
        }

        if (reason.Length > Order.AbandonReasonMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = [$"At most {Order.AbandonReasonMaxLength} characters."],
            });
        }

        var now = timeProvider.GetUtcNow();

        // Abandoned, deliberately not Closed. Whatever was fired was cooked and is gone, and the
        // shop needs that visible as a loss rather than folded into a day's takings.
        order.Status = OrderStatus.Abandoned;
        order.ClosedAt = now;
        order.ClosedBy = actor.UserId;
        order.AbandonReason = reason;

        audit.Record(
            AuditAction.OrderAbandoned,
            nameof(Order),
            order.Id,
            after: new Dictionary<string, string?>
            {
                ["orderNumber"] = order.OrderNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["reason"] = reason,
            });

        idempotency.Record(db, StatusCodes.Status200OK, new { orderId = id }, now);

        await db.SaveChangesAsync(cancellationToken);

        var lines = await LinesAsync(db, id, cancellationToken);
        var tableName = await TableNameAsync(db, order.DiningTableId, cancellationToken);

        return TypedResults.Ok(Project(order, tableName, lines));
    }

    /// <summary>
    /// Resolves requested lines against the catalog, snapshotting price and rate.
    /// </summary>
    /// <remarks>
    /// One read for the whole batch, joined to the tax class, so a round of drinks is not a round
    /// trip per glass — the same shape <c>SaleEndpoints.BuildCartAsync</c> uses.
    /// <para>
    /// <b>The snapshot is taken here, when the item is ordered.</b> A guest who ordered at 18:00
    /// pays the 18:00 price even if the menu changes at 19:00, and the bill copies these values
    /// forward rather than re-reading the catalog.
    /// </para>
    /// </remarks>
    private static async Task<(List<OrderLineInstruction> Lines, Dictionary<string, string[]> Errors)>
        ResolveAsync(
            AppDbContext db,
            IReadOnlyList<AddOrderLineRequest> requested,
            string path,
            CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var ids = Flatten(requested)
            .Where(l => l.ProductId is not null)
            .Select(l => l.ProductId!.Value)
            .Distinct()
            .ToArray();

        var catalog = await (
            from product in db.Products.AsNoTracking()
            join taxClass in db.TaxClasses on product.TaxClassId equals taxClass.Id
            where ids.Contains(product.Id)
            select new MenuItem(
                product.Id,
                product.Name,
                product.UnitPrice,
                product.IsActive,
                taxClass.Rate)).ToDictionaryAsync(p => p.Id, cancellationToken);

        var resolved = new List<OrderLineInstruction>();

        for (var index = 0; index < requested.Count; index++)
        {
            var line = Resolve(requested[index], $"{path}[{index}]", catalog, errors, depth: 0);

            if (line is not null)
            {
                resolved.Add(line);
            }
        }

        return (resolved, errors);

        static IEnumerable<AddOrderLineRequest> Flatten(IReadOnlyList<AddOrderLineRequest> lines)
        {
            foreach (var line in lines)
            {
                yield return line;

                foreach (var modifier in line.Modifiers ?? [])
                {
                    yield return modifier;
                }
            }
        }
    }

    /// <summary>One menu item, resolved against the catalog with its tax rate.</summary>
    private sealed record MenuItem(Guid Id, string Name, Money UnitPrice, bool IsActive, decimal Rate);

    private static OrderLineInstruction? Resolve(
        AddOrderLineRequest request,
        string path,
        Dictionary<Guid, MenuItem> catalog,
        Dictionary<string, string[]> errors,
        int depth)
    {
        if (request.ProductId is not { } productId || !catalog.TryGetValue(productId, out var product))
        {
            errors[$"{path}.productId"] = ["No product with that id exists in this shop."];
            return null;
        }

        if (!product.IsActive)
        {
            errors[$"{path}.productId"] = ["That product is not on the menu."];
            return null;
        }

        var quantity = request.Quantity ?? 1m;

        if (quantity <= 0m || !CatalogRules.IsStorableAmount(quantity))
        {
            errors[$"{path}.quantity"] = ["A quantity greater than zero with at most 4 decimal places is required."];
            return null;
        }

        var course = request.Course ?? 1;

        if (course < 1)
        {
            errors[$"{path}.course"] = ["Courses start at 1."];
            return null;
        }

        if (request.SeatNumber is { } seat && seat < 1)
        {
            errors[$"{path}.seatNumber"] = ["Seats start at 1."];
            return null;
        }

        var overridden = request.UnitPriceOverride is not null;

        if (overridden && (request.UnitPriceOverride!.Value < 0m
            || !CatalogRules.IsStorableAmount(request.UnitPriceOverride.Value)))
        {
            errors[$"{path}.unitPriceOverride"] = ["A price of 0 or more with at most 4 decimal places is required."];
            return null;
        }

        // The snapshot. The catalog's price unless a manager typed one, and never read again.
        var unitPrice = overridden ? (Money)request.UnitPriceOverride!.Value : product.UnitPrice;

        var discount = request.DiscountAmount ?? 0m;

        if (discount < 0m || !CatalogRules.IsStorableAmount(discount))
        {
            errors[$"{path}.discountAmount"] = ["A discount of 0 or more with at most 4 decimal places is required."];
            return null;
        }

        List<OrderLineInstruction>? modifiers = null;

        if (request.Modifiers is { Count: > 0 } children)
        {
            if (depth > 0)
            {
                // One level deep, by rule. A modifier of a modifier is a menu that needs
                // rethinking rather than a data structure that needs recursion — and the kitchen
                // ticket has nowhere to print the third level anyway.
                errors[$"{path}.modifiers"] = ["A modifier cannot itself have modifiers."];
                return null;
            }

            modifiers = [];

            for (var index = 0; index < children.Count; index++)
            {
                var child = Resolve(children[index], $"{path}.modifiers[{index}]", catalog, errors, depth + 1);

                if (child is not null)
                {
                    modifiers.Add(child);
                }
            }
        }

        var note = request.Note?.Trim();

        if (note is { Length: > OrderLine.NoteMaxLength })
        {
            errors[$"{path}.note"] = [$"At most {OrderLine.NoteMaxLength} characters."];
            return null;
        }

        return new OrderLineInstruction(
            productId,
            product.Name,
            quantity,
            unitPrice,
            product.Rate,
            (Money)discount,
            course,
            request.SeatNumber,
            string.IsNullOrEmpty(note) ? null : note,
            overridden,
            OverriddenBy: null,
            modifiers);
    }

    /// <summary>
    /// Checks each line's chosen modifiers against the questions its product asks.
    /// </summary>
    /// <remarks>
    /// The rule itself is <see cref="ModifierRules"/> — pure, in <c>Pos.Core</c>, testable
    /// without a database. What happens here is only the reading: one query for every group the
    /// batch touches, so a round of six drinks is one round trip rather than six.
    /// <para>
    /// Products with no groups are skipped entirely, which is every product in a retail shop and
    /// most of them in a restaurant. A round of beers costs nothing extra.
    /// </para>
    /// </remarks>
    private static async Task CheckModifiersAsync(
        AppDbContext db,
        List<OrderLineInstruction> lines,
        string path,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        var parentIds = lines.Select(l => l.ProductId).Distinct().ToArray();

        var links = await db.ProductModifierGroups
            .AsNoTracking()
            .Where(link => parentIds.Contains(link.ProductId))
            .OrderBy(link => link.SortOrder)
            .ToListAsync(cancellationToken);

        if (links.Count == 0)
        {
            // Nothing on this order asks a question. Every retail product, and a round of beers.
            return;
        }

        var groupIds = links.Select(link => link.ModifierGroupId).Distinct().ToArray();

        var groups = await db.ModifierGroups
            .AsNoTracking()
            .Where(g => groupIds.Contains(g.Id) && g.IsActive)
            .ToDictionaryAsync(g => g.Id, cancellationToken);

        var options = await db.ModifierOptions
            .AsNoTracking()
            .Where(o => groupIds.Contains(o.ModifierGroupId))
            .ToListAsync(cancellationToken);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];

            var rules = links
                .Where(link => link.ProductId == line.ProductId)
                .Where(link => groups.ContainsKey(link.ModifierGroupId))
                .Select(link => new ModifierGroupRule(
                    link.ModifierGroupId,
                    groups[link.ModifierGroupId].Name,
                    groups[link.ModifierGroupId].MinSelections,
                    groups[link.ModifierGroupId].MaxSelections,
                    options
                        .Where(o => o.ModifierGroupId == link.ModifierGroupId)
                        .Select(o => o.ProductId)
                        .ToHashSet()))
                .ToList();

            if (rules.Count == 0)
            {
                continue;
            }

            var chosen = (line.Modifiers ?? []).Select(m => m.ProductId).ToList();
            var violations = ModifierRules.Check(rules, chosen);

            if (violations.Count > 0)
            {
                errors[$"{path}[{index}].modifiers"] =
                    [.. violations.Select(v => v.Message)];
            }
        }
    }

    /// <summary>Which adjustment policies this batch needs and the caller does not hold.</summary>
    private static async Task<List<string>> MissingAdjustmentPoliciesAsync(
        IReadOnlyList<OrderLineInstruction> lines,
        ClaimsPrincipal caller,
        IAuthorizationService authorization)
    {
        var required = new List<string>();

        var all = lines.SelectMany(l => new[] { l }.Concat(l.Modifiers ?? [])).ToList();

        if (all.Any(l => !l.DiscountAmount.IsZero)
            && !(await authorization.AuthorizeAsync(caller, Policies.CanApplyDiscount)).Succeeded)
        {
            required.Add(Policies.CanApplyDiscount);
        }

        if (all.Any(l => l.IsPriceOverridden)
            && !(await authorization.AuthorizeAsync(caller, Policies.CanOverridePrice)).Succeeded)
        {
            required.Add(Policies.CanOverridePrice);
        }

        return required;
    }

    private static ProblemHttpResult AdjustmentNotAuthorized(IReadOnlyList<string> missing) =>
        TypedResults.Problem(
            title: "A manager has to authorise this.",
            detail: "This action needs a permission the signed-in user does not hold.",
            statusCode: StatusCodes.Status403Forbidden,
            type: "https://pos.example/errors/override-required",
            extensions: new Dictionary<string, object?> { ["requiredPolicies"] = missing });

    private static async Task<IReadOnlyList<OrderLineResponse>> LinesAsync(
        AppDbContext db,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var lines = await db.OrderLines
            .AsNoTracking()
            .Where(l => l.OrderId == orderId)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

        return [.. lines.Select(Project)];
    }

    private static async Task<string?> TableNameAsync(
        AppDbContext db,
        Guid? tableId,
        CancellationToken cancellationToken)
    {
        if (tableId is not { } id)
        {
            return null;
        }

        return await db.DiningTables
            .AsNoTracking()
            .Where(t => t.Id == id)
            .Select(t => t.Name)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private static async Task<Dictionary<Guid, string>> TableNamesAsync(
        AppDbContext db,
        CancellationToken cancellationToken) =>
        await db.DiningTables
            .AsNoTracking()
            .ToDictionaryAsync(t => t.Id, t => t.Name, cancellationToken);

    internal static OrderResponse Project(
        Order order,
        string? tableName,
        IReadOnlyList<OrderLineResponse> lines) =>
        new(
            order.Id,
            order.OrderNumber,
            order.Type,
            order.Status,
            order.DiningTableId,
            tableName,
            order.TabName,
            order.CoverCount,
            order.Note,
            order.OpenedBy,
            order.OpenedAt,
            order.ClosedAt,
            lines);

    internal static OrderLineResponse Project(OrderLine line) =>
        new(
            line.Id,
            line.LineNumber,
            line.ProductId,
            line.ParentOrderLineId,
            line.Description,
            line.Quantity,
            (decimal)line.UnitPrice,
            line.TaxRate,
            (decimal)line.DiscountAmount,
            line.Course,
            line.SeatNumber,
            line.Note,
            line.Status,
            line.FiredAt);
}
