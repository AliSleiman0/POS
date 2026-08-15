using Microsoft.EntityFrameworkCore;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Menus;
using Pos.Core.Tenancy;

namespace Pos.Data.Orders;

/// <summary>A ticket and the items on it, as the fire that created them returns it.</summary>
public sealed record FiredTicket(KitchenTicket Ticket, IReadOnlyList<KitchenTicketLine> Lines);

/// <summary>
/// Sends a round of an order to the kitchen, one ticket per station.
/// </summary>
/// <remarks>
/// <b>Not a Core port, for the reason <c>OrderWriter</c> and <c>ShiftWriter</c> both give.</b> The
/// rule worth owning — where an item is cooked — is <see cref="StationRouting"/>, which is pure,
/// in <c>Pos.Core</c>, and reads nothing. What is left here is a row lock, some catalog reads and
/// a batch insert, which is not a rule and does not need an interface in front of it.
/// <para>
/// <b>Firing is idempotent by construction, not by an idempotency key.</b> It acts on
/// <see cref="OrderLineStatus.Pending"/> lines and marks them <see cref="OrderLineStatus.Fired"/>
/// in the same transaction, so a second tap finds nothing pending and creates nothing — and that
/// holds for a double-tap from a different device with a different key, which a stored response
/// would not cover. The endpoint carries a key as well, because a replay should return the
/// original ticket list rather than an empty one, but the key is the courtesy and the status is
/// the guarantee.
/// </para>
/// <para>
/// <b>Nothing here is money and nothing here touches stock.</b> Phase 10's rule 4: stock moves at
/// payment, inside the sale's transaction, and a fired-then-voided item is a <c>Waste</c> movement
/// a shop records deliberately — not an automatic reversal, because inventing compensating
/// movements for food that may or may not have been cooked is worse data than none.
/// </para>
/// </remarks>
public sealed class KitchenTicketWriter(
    AppDbContext db,
    ITenantContext tenant,
    ICurrentActor actor,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Fires one course, or everything still pending.
    /// </summary>
    /// <param name="orderId">The order. Must be open.</param>
    /// <param name="course">The round to send, or null for every pending line on the order.</param>
    /// <param name="onCommitting">Runs inside the transaction, once the tickets have ids.</param>
    /// <returns>The tickets created, station order. Empty when there was nothing left to fire.</returns>
    /// <exception cref="OrderNotOpenException">It has been settled or abandoned.</exception>
    /// <exception cref="ProductNotRoutedException">Something on it has no station.</exception>
    public async Task<IReadOnlyList<FiredTicket>> FireAsync(
        Guid orderId,
        int? course,
        Action<IReadOnlyList<FiredTicket>>? onCommitting,
        CancellationToken cancellationToken)
    {
        var firedAt = timeProvider.GetUtcNow();

        var firedBy = actor.UserId
            ?? throw new InvalidOperationException(
                "A ticket needs somebody to have fired it, and no user is attached to this request.");

        List<FiredTicket> fired = [];

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            fired.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // The same lock OrderWriter.AddLinesAsync takes, and for a stronger reason: without
            // it two waiters tapping "fire" on one table in the same second both read the same
            // pending lines and both insert tickets, and the grill cooks the round twice.
            await LockOpenOrderAsync(orderId, cancellationToken);

            var order = await db.Orders
                .AsNoTracking()
                .FirstAsync(o => o.Id == orderId, cancellationToken);

            // Tracked, because the ones that go get their status changed in this transaction.
            var lines = await db.OrderLines
                .Where(l => l.OrderId == orderId)
                .OrderBy(l => l.LineNumber)
                .ToListAsync(cancellationToken);

            // Parents only. A modifier travels with the item it modifies — it is a phrase under
            // the item on the ticket, never a row of its own — so the round is decided by the
            // parent's course and a child's own course is not consulted.
            var going = lines
                .Where(l => l.ParentOrderLineId is null
                    && l.Status == OrderLineStatus.Pending
                    && (course is not { } wanted || l.Course == wanted))
                .ToList();

            if (going.Count == 0)
            {
                // Not an error. This is the second tap, and the honest answer to "what did that
                // send?" is "nothing" — the round is already in the kitchen.
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var stations = await ResolveStationsAsync(going, cancellationToken);

            var label = LabelFor(order, await TableNameAsync(order.DiningTableId, cancellationToken));

            var childrenByParent = lines
                .Where(l => l.ParentOrderLineId is not null && l.Status != OrderLineStatus.Voided)
                .GroupBy(l => l.ParentOrderLineId!.Value)
                .ToDictionary(g => g.Key, g => g.OrderBy(l => l.LineNumber).ToList());

            // One ticket per station per course. Two courses fired at once are two tickets to the
            // grill, because a ticket spanning rounds is a queue the pass cannot pace.
            var rounds = going
                .GroupBy(l => (Station: stations[l.ProductId], l.Course))
                .OrderBy(g => g.Key.Course);

            foreach (var round in rounds)
            {
                var ticket = new KitchenTicket
                {
                    OrderId = orderId,
                    StationId = round.Key.Station,
                    Course = round.Key.Course,
                    OrderNumber = order.OrderNumber,
                    OrderLabel = label,
                    FiredAt = firedAt,
                    FiredBy = firedBy,
                    Status = KitchenTicketStatus.Active,
                };

                db.KitchenTickets.Add(ticket);

                // Saved before its lines so the id exists to point at. There are no navigation
                // properties in this model, so EF does no foreign-key fixup — see OrderWriter.
                await db.SaveChangesAsync(cancellationToken);

                List<KitchenTicketLine> ticketLines = [];

                foreach (var line in round.OrderBy(l => l.LineNumber))
                {
                    var children = childrenByParent.TryGetValue(line.Id, out var found) ? found : [];

                    var ticketLine = new KitchenTicketLine
                    {
                        KitchenTicketId = ticket.Id,
                        OrderLineId = line.Id,
                        LineNumber = line.LineNumber,
                        Description = line.Description,
                        Quantity = line.Quantity,
                        SeatNumber = line.SeatNumber,
                        ModifierText = ComposeModifiers(children),
                        Note = line.Note,
                    };

                    db.KitchenTicketLines.Add(ticketLine);
                    ticketLines.Add(ticketLine);

                    line.Status = OrderLineStatus.Fired;
                    line.FiredAt = firedAt;

                    // The modifiers go with it. They were never separately pending as far as the
                    // kitchen is concerned, and leaving them Pending would let a later fire send
                    // "extra cheese" on a ticket of its own with no burger under it.
                    foreach (var child in children.Where(c => c.Status == OrderLineStatus.Pending))
                    {
                        child.Status = OrderLineStatus.Fired;
                        child.FiredAt = firedAt;
                    }
                }

                fired.Add(new FiredTicket(ticket, ticketLines));
            }

            await db.SaveChangesAsync(cancellationToken);

            onCommitting?.Invoke(fired);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return fired;
    }

    /// <summary>
    /// Where each line's product is cooked, refusing the whole fire if anything routes nowhere.
    /// </summary>
    /// <remarks>
    /// The category chain is walked in memory over every category in the shop, which is tens of
    /// rows and one query — rather than a recursive CTE for a hierarchy that is three deep in
    /// practice. The visited set is belt and braces: <c>CategoryEndpoints</c> refuses a cycle when
    /// a parent is set, and nothing in SQL can guarantee it stayed acyclic.
    /// </remarks>
    private async Task<Dictionary<Guid, Guid>> ResolveStationsAsync(
        IReadOnlyList<OrderLine> lines,
        CancellationToken cancellationToken)
    {
        var productIds = lines.Select(l => l.ProductId).Distinct().ToList();

        var products = await db.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.StationId, p.CategoryId })
            .ToListAsync(cancellationToken);

        var categories = await db.Categories
            .AsNoTracking()
            .Select(c => new { c.Id, c.ParentCategoryId, c.StationId })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        Dictionary<Guid, Guid> resolved = [];
        List<string> unrouted = [];

        foreach (var product in products)
        {
            List<Guid?> chain = [];
            HashSet<Guid> visited = [];

            var next = product.CategoryId;

            while (next is { } categoryId
                && visited.Add(categoryId)
                && categories.TryGetValue(categoryId, out var category))
            {
                chain.Add(category.StationId);
                next = category.ParentCategoryId;
            }

            if (StationRouting.Resolve(product.StationId, chain) is { } station)
            {
                resolved[product.Id] = station;
            }
            else
            {
                unrouted.Add(product.Name);
            }
        }

        if (unrouted.Count > 0)
        {
            // The whole fire, not the offending line. Firing half a round would put the rest of
            // the table in the kitchen with no record of what was dropped, and the waiter would
            // have no way to tell which items are cooking. See the exception.
            throw ProductNotRoutedException.For([.. unrouted.Order(StringComparer.Ordinal)]);
        }

        return resolved;
    }

    /// <summary>
    /// The modifiers as one line of text — "extra cheese, 2 × bacon".
    /// </summary>
    /// <remarks>
    /// Composed here rather than stored as child rows: on a ticket a modifier is a phrase under
    /// the item, not a thing with a price, and rows would be a second parent/child tree to keep
    /// in step with the first for a reader that immediately flattens it.
    /// <para>
    /// Truncated rather than allowed to hit the check constraint. A ticket with a clipped list is
    /// a ticket a chef can still work from; a 500 during service is not.
    /// </para>
    /// </remarks>
    private static string? ComposeModifiers(List<OrderLine> children)
    {
        if (children.Count == 0)
        {
            return null;
        }

        var text = string.Join(
            ", ",
            children.Select(c => c.Quantity == 1m
                ? c.Description
                : $"{c.Quantity:0.####} × {c.Description}"));

        return text.Length <= KitchenTicketLine.ModifierTextMaxLength
            ? text
            : text[..(KitchenTicketLine.ModifierTextMaxLength - 1)] + "…";
    }

    /// <summary>
    /// Where the food is going, as a person reads it.
    /// </summary>
    /// <remarks>
    /// A takeaway has no table and no tab name, and the kitchen still has to be told something —
    /// so the type's own name is the answer rather than an empty header nobody can act on.
    /// </remarks>
    private static string LabelFor(Order order, string? tableName)
    {
        var label = order.Type switch
        {
            OrderType.Table => tableName is null ? $"Order {order.OrderNumber}" : $"Table {tableName}",
            OrderType.Tab => order.TabName ?? $"Tab {order.OrderNumber}",
            _ => "Takeaway",
        };

        return label.Length <= KitchenTicket.OrderLabelMaxLength
            ? label
            : label[..KitchenTicket.OrderLabelMaxLength];
    }

    private async Task<string?> TableNameAsync(Guid? tableId, CancellationToken cancellationToken)
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

    /// <summary>
    /// Takes an exclusive lock on one order and refuses if it is not open.
    /// </summary>
    /// <remarks>
    /// Raw SQL because the lock is the point and EF has no <c>FOR UPDATE</c>. The
    /// <c>tenant_id</c> predicate is written by hand for the reason <c>OrderWriter</c> states:
    /// the global query filter composes over LINQ and not over this. The tenant still comes from
    /// the validated token, and row-level security is underneath.
    /// </remarks>
    private async Task LockOpenOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var statuses = await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT status AS "Value" FROM customer_order
                 WHERE tenant_id = {tenant.TenantId} AND id = {orderId}
                 FOR UPDATE
                 """)
            .ToListAsync(cancellationToken);

        if (statuses.Count == 0)
        {
            throw new OrderNotOpenException("That order no longer exists.");
        }

        if (!string.Equals(statuses[0], nameof(OrderStatus.Open), StringComparison.Ordinal))
        {
            throw new OrderNotOpenException();
        }
    }
}
