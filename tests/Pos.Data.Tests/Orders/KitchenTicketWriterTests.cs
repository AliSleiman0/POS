using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Data.Orders;
using Pos.Data.Tests.Catalog;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Orders;

/// <summary>
/// The fire's guard, tested where it actually is — under the lock, with two writers racing.
/// </summary>
/// <remarks>
/// <c>KitchenTests</c> proves the API is idempotent by firing twice in sequence, which is the
/// case a waiter produces. It cannot prove the case a <i>kitchen</i> produces: two handhelds
/// tapping fire in the same second, where both transactions read the same pending lines. That is
/// what the <c>FOR UPDATE</c> on the order row is for, and per CLAUDE.md invariant 9 a
/// concurrency test has to actually run concurrently to say anything about it.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class KitchenTicketWriterTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Two_handhelds_firing_at_once_send_the_round_once()
    {
        var world = await ArrangeAsync();

        await using var one = Scope(world);
        await using var two = Scope(world);

        var barrier = new Barrier(2);

        async Task<int> FireAsync(ScopedDbContext scoped)
        {
            await Task.Yield();
            barrier.SignalAndWait();

            var fired = await scoped.Services
                .GetRequiredService<KitchenTicketWriter>()
                .FireAsync(world.OrderId, course: 1, null, CancellationToken.None);

            return fired.Count;
        }

        var counts = await Task.WhenAll(FireAsync(one), FireAsync(two));

        // One winner and one that found nothing left to send. Neither fails: the loser did not
        // do anything wrong, it arrived second — and a 409 at the pass would send a waiter
        // looking for a problem that does not exist.
        Assert.Equal(1, counts.Count(count => count == 1));
        Assert.Equal(1, counts.Count(count => count == 0));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        // The claim that matters: the grill was told once. Two tickets here is a plate cooked
        // twice and thrown away, every time two people reach for the screen together.
        Assert.Single(await reader.Db.KitchenTickets.ToListAsync());
        Assert.Single(await reader.Db.KitchenTicketLines.ToListAsync());
    }

    [Fact]
    public async Task Nothing_is_written_when_something_has_no_station()
    {
        var world = await ArrangeAsync(route: false);

        await using var scoped = Scope(world);

        await Assert.ThrowsAsync<ProductNotRoutedException>(
            () => scoped.Services
                .GetRequiredService<KitchenTicketWriter>()
                .FireAsync(world.OrderId, course: null, null, CancellationToken.None));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        Assert.Empty(await reader.Db.KitchenTickets.ToListAsync());

        // And the line is still the waiter's to fire once the menu is fixed, rather than marked
        // away with nothing in the kitchen to match it.
        var line = await reader.Db.OrderLines.SingleAsync();

        Assert.Equal(OrderLineStatus.Pending, line.Status);
        Assert.Null(line.FiredAt);
    }

    [Fact]
    public async Task A_ticket_is_not_written_for_an_order_that_has_been_settled()
    {
        var world = await ArrangeAsync();

        await using (var closing = Scope(world))
        {
            var order = await closing.Db.Orders.FirstAsync(o => o.Id == world.OrderId);
            order.Status = OrderStatus.Closed;
            await closing.Db.SaveChangesAsync();
        }

        await using var scoped = Scope(world);

        await Assert.ThrowsAsync<OrderNotOpenException>(
            () => scoped.Services
                .GetRequiredService<KitchenTicketWriter>()
                .FireAsync(world.OrderId, course: null, null, CancellationToken.None));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        Assert.Empty(await reader.Db.KitchenTickets.ToListAsync());
    }

    private ScopedDbContext Scope(KitchenWorld world) =>
        ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId, world.WaiterId);

    private sealed record KitchenWorld(Guid TenantId, Guid WaiterId, Guid OrderId);

    private async Task<KitchenWorld> ArrangeAsync(bool route = true)
    {
        var tenantId = Guid.CreateVersion7();
        var waiterId = Guid.CreateVersion7();

        await using (var owner = ScopedDbContext.WithoutTenant(postgres.ConnectionString))
        {
            owner.Db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Kitchen Writer Tenant",
                Slug = $"k-{tenantId:N}"[..20],
                CurrencyCode = "EUR",
                TimeZoneId = "Europe/Dublin",
                TaxMode = TaxMode.Inclusive,
                ServiceMode = ServiceMode.Restaurant,
            });

            await owner.Db.SaveChangesAsync();
        }

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, tenantId, waiterId);

        var catalog = await CatalogGraph.WriteAsync(scoped.Db);

        if (route)
        {
            var station = new Station { Name = "Pass", SortOrder = 1 };
            scoped.Db.Stations.Add(station);
            await scoped.Db.SaveChangesAsync();

            var product = await scoped.Db.Products.FirstAsync(p => p.Id == catalog.ProductId);
            product.StationId = station.Id;
        }

        var order = new Order
        {
            OrderNumber = 1,
            Type = OrderType.Takeaway,
            Status = OrderStatus.Open,
            OpenedBy = waiterId,
            OpenedAt = DateTimeOffset.UnixEpoch,
        };

        scoped.Db.Orders.Add(order);
        await scoped.Db.SaveChangesAsync();

        scoped.Db.OrderLines.Add(new OrderLine
        {
            OrderId = order.Id,
            ProductId = catalog.ProductId,
            LineNumber = 1,
            Description = "Soup",
            Quantity = 1m,
            UnitPrice = (Money)5.50m,
            TaxRate = 0.2300m,
            DiscountAmount = Money.Zero,
            Course = 1,
            Status = OrderLineStatus.Pending,
        });

        await scoped.Db.SaveChangesAsync();

        return new KitchenWorld(tenantId, waiterId, order.Id);
    }
}
