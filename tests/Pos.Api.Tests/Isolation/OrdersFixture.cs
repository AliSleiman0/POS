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
public sealed record SeededOrders(
    Guid AreaId,
    IReadOnlyList<Guid> TableIds,
    Guid SeatedTableId,
    Guid FreeTableId,
    Guid OpenOrderId,
    Guid OpenOrderLineId,
    Guid SecondOrderId)
{
    /// <summary>Everything <c>GET /orders</c> must return for this tenant, and nothing else.</summary>
    public IReadOnlyList<Guid> OpenOrderIds => [OpenOrderId, SecondOrderId];
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
        await db.SaveChangesAsync();

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

        return new SeededOrders(
            area.Id,
            [seated.Id, free.Id],
            seated.Id,
            free.Id,
            order.Id,
            line.Id,
            tab.Id);
    }
}
