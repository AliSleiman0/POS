using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data;

namespace Pos.Api.Tests.Isolation;

/// <summary>The floor and the orders on it, identical in both isolation tenants.</summary>
/// <param name="AreaId">The one service area.</param>
/// <param name="TableIds">Two tables: one seated, one free.</param>
/// <param name="SeatedTableId">The table the open order is on.</param>
/// <param name="FreeTableId">A table with nothing on it, so a transfer has somewhere to go.</param>
/// <param name="OpenOrderId">An order with lines, for every by-id route.</param>
/// <param name="OpenOrderLineId">A pending line on it.</param>
/// <param name="SecondOrderId">A tab, so <c>GET /orders</c> has more than one row to get wrong.</param>
/// <param name="StationId">The one station, which the seeded product routes to.</param>
/// <param name="KitchenTicketId">A ticket on that station's screen, for every by-id kitchen route.</param>
public sealed record SeededOrders(
    Guid AreaId,
    IReadOnlyList<Guid> TableIds,
    Guid SeatedTableId,
    Guid FreeTableId,
    Guid OpenOrderId,
    Guid OpenOrderLineId,
    Guid SecondOrderId,
    Guid StationId,
    Guid KitchenTicketId)
{
    /// <summary>Everything <c>GET /orders</c> must return for this tenant, and nothing else.</summary>
    public IReadOnlyList<Guid> OpenOrderIds => [OpenOrderId, SecondOrderId];

    /// <summary>Everything <c>GET /kitchen/tickets</c> must return, and nothing else.</summary>
    public IReadOnlyList<Guid> KitchenTicketIds => [KitchenTicketId];

    /// <summary>Everything <c>GET /stations</c> must return, and nothing else.</summary>
    public IReadOnlyList<Guid> StationIds => [StationId];
}

/// <summary>
/// Seeds a room and two open orders, through the <c>DbContext</c> rather than the endpoints.
/// </summary>
/// <remarks>
/// Written directly for the reason <c>SalesFixture</c> is: the endpoints are what this suite is
/// attacking, and a fixture built from them would fail to arrange whenever the thing under test
/// was broken — which is exactly when the arrangement needs to be trustworthy.
/// </remarks>
public static class OrdersFixture
{
    public static async Task<SeededOrders> WriteAsync(
        PosApiFactory factory,
        Guid tenantId,
        Guid waiterId,
        SeededCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(catalog);

        SeededOrders? seeded = null;

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            seeded = await WriteAsync(db, waiterId, catalog);
        });

        return seeded!;
    }

    private static async Task<SeededOrders> WriteAsync(
        AppDbContext db,
        Guid waiterId,
        SeededCatalog catalog)
    {
        var area = new ServiceArea { Name = "Main room", SortOrder = 1 };
        db.ServiceAreas.Add(area);

        var station = new Station { Name = "Pass", SortOrder = 1 };
        db.Stations.Add(station);

        await db.SaveChangesAsync();

        // The seeded product is routed on itself rather than through its category, so a fire in
        // this world does not depend on what CatalogFixture happens to have put the water in.
        var water = await db.Products.FirstAsync(p => p.Id == catalog.WaterProductId);
        water.StationId = station.Id;

        var seated = new DiningTable { ServiceAreaId = area.Id, Name = "1", Seats = 4, SortOrder = 1 };
        var free = new DiningTable { ServiceAreaId = area.Id, Name = "2", Seats = 2, SortOrder = 2 };

        db.DiningTables.AddRange(seated, free);
        await db.SaveChangesAsync();

        var openedAt = new DateTimeOffset(2026, 8, 15, 18, 0, 0, TimeSpan.Zero);

        var order = new Order
        {
            OrderNumber = 1,
            Type = OrderType.Table,
            Status = OrderStatus.Open,
            DiningTableId = seated.Id,
            OpenedBy = waiterId,
            OpenedAt = openedAt,
            CoverCount = 2,
        };

        var tab = new Order
        {
            OrderNumber = 2,
            Type = OrderType.Tab,
            Status = OrderStatus.Open,
            TabName = "Sarah, red coat",
            OpenedBy = waiterId,
            OpenedAt = openedAt.AddMinutes(5),
            CoverCount = 1,
        };

        db.Orders.AddRange(order, tab);

        // The counter is written by hand so the numbers above do not collide with the next
        // order the endpoints open — the writer's upsert reads this row, and a missing one
        // would hand out 1 again.
        db.OrderSequences.Add(new OrderSequence { LastNumber = 2 });

        await db.SaveChangesAsync();

        var line = new OrderLine
        {
            OrderId = order.Id,
            ProductId = catalog.WaterProductId,
            LineNumber = 1,
            Description = "Still Water 500ml",
            Quantity = 1m,
            UnitPrice = (Money)1.2000m,
            TaxRate = 0.2300m,
            DiscountAmount = Money.Zero,
            Course = 1,
            Status = OrderLineStatus.Pending,
        };

        db.OrderLines.Add(line);
        await db.SaveChangesAsync();

        // A second line, already fired, so there is a ticket to attack by id without the pending
        // line above being fired — which every fire test would then have nothing to send.
        var firedLine = new OrderLine
        {
            OrderId = order.Id,
            ProductId = catalog.WaterProductId,
            LineNumber = 2,
            Description = "Still Water 500ml",
            Quantity = 1m,
            UnitPrice = (Money)1.2000m,
            TaxRate = 0.2300m,
            DiscountAmount = Money.Zero,
            Course = 2,
            Status = OrderLineStatus.Fired,
            FiredAt = openedAt.AddMinutes(2),
        };

        db.OrderLines.Add(firedLine);
        await db.SaveChangesAsync();

        var ticket = new KitchenTicket
        {
            OrderId = order.Id,
            StationId = station.Id,
            Course = 2,
            OrderNumber = order.OrderNumber,
            OrderLabel = "Table 1",
            FiredAt = openedAt.AddMinutes(2),
            FiredBy = waiterId,
            Status = KitchenTicketStatus.Active,
        };

        db.KitchenTickets.Add(ticket);
        await db.SaveChangesAsync();

        db.KitchenTicketLines.Add(new KitchenTicketLine
        {
            KitchenTicketId = ticket.Id,
            OrderLineId = firedLine.Id,
            LineNumber = firedLine.LineNumber,
            Description = firedLine.Description,
            Quantity = firedLine.Quantity,
        });

        await db.SaveChangesAsync();

        return new SeededOrders(
            area.Id,
            [seated.Id, free.Id],
            seated.Id,
            free.Id,
            order.Id,
            line.Id,
            tab.Id,
            station.Id,
            ticket.Id);
    }
}
