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
/// The order writer's guards, tested without an endpoint in front of them.
/// </summary>
/// <remarks>
/// Same reasoning as <c>SaleWriterTests</c>: the endpoints validate before calling in, so an
/// endpoint test cannot tell whether the writer's own checks do anything — deleting them would
/// leave the API suite green. The endpoint's check exists to produce a good message; the writer's
/// exists to be the guarantee, under the lock, where a concurrent close is decided.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class OrderWriterTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Order_numbers_are_sequential_from_one_and_scoped_to_the_tenant()
    {
        var first = await ArrangeAsync();
        var second = await ArrangeAsync();

        await using var one = Scope(first);
        await using var two = Scope(second);

        var a1 = await Writer(one).OpenAsync(TableOrder(first), null, CancellationToken.None);
        var a2 = await Writer(one).OpenAsync(Takeaway(), null, CancellationToken.None);
        var b1 = await Writer(two).OpenAsync(TableOrder(second), null, CancellationToken.None);

        Assert.Equal(1, a1.OrderNumber);
        Assert.Equal(2, a2.OrderNumber);

        // The second shop starts at one as well. A number is per tenant, and a global sequence
        // would leak how much business the platform's other customers are doing.
        Assert.Equal(1, b1.OrderNumber);
    }

    [Fact]
    public async Task An_order_number_series_is_separate_from_the_sale_number_series()
    {
        // Load-bearing, and the reason OrderSequence exists rather than reusing SaleSequence.
        // One order can settle as three bills and an abandoned one settles as none, so a shared
        // counter would scatter unexplained gaps through the sale numbers — which is exactly
        // what that counter exists to prevent, because gaps read as deleted records.
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);
        await Writer(scoped).OpenAsync(Takeaway(), null, CancellationToken.None);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        Assert.Equal(2, (await reader.Db.OrderSequences.SingleAsync()).LastNumber);

        // Untouched: no sale has happened.
        Assert.Empty(await reader.Db.SaleSequences.ToListAsync());
    }

    [Fact]
    public async Task A_second_open_order_on_one_table_is_refused_by_the_index()
    {
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);

        /*
         * The filtered unique index is the authority, not a pre-check.
         *
         * Two staff seating the same table in the same second both pass "is anything open
         * here?" and both insert, and the table ends up carrying two bills — the next round of
         * drinks joins whichever one the query returned, and nobody notices until one is paid
         * and the other is not.
         *
         * The writer lets the violation out; OrderEndpoints turns it into a
         * TableAlreadyOccupiedException, exactly as ShiftEndpoints does for a second drawer.
         */
        var collision = await Assert.ThrowsAsync<DbUpdateException>(
            () => Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None));

        Assert.Contains(
            "ux_customer_order_tenant_table_open",
            collision.InnerException?.Message ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_closed_table_can_be_seated_again()
    {
        // The other half of the index's filter, and the case that would break if it keyed on
        // the table alone: a restaurant turns a table several times an evening.
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        var first = await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);

        var stored = await scoped.Db.Orders.SingleAsync(o => o.Id == first.Id);
        stored.Status = OrderStatus.Closed;
        stored.ClosedAt = DateTimeOffset.UtcNow;
        await scoped.Db.SaveChangesAsync();

        var second = await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(world.TableId, second.DiningTableId);
    }

    [Fact]
    public async Task Several_tabs_are_open_at_once_because_they_have_no_table()
    {
        // A bar runs a dozen slates simultaneously. The index's filter says
        // `dining_table_id IS NOT NULL` for this reason.
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        await Writer(scoped).OpenAsync(Tab("Sarah, red coat"), null, CancellationToken.None);
        await Writer(scoped).OpenAsync(Tab("Two at the window"), null, CancellationToken.None);
        await Writer(scoped).OpenAsync(Takeaway(), null, CancellationToken.None);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        Assert.Equal(3, await reader.Db.Orders.CountAsync(o => o.Status == OrderStatus.Open));
    }

    [Fact]
    public async Task Lines_are_numbered_in_sequence_and_modifiers_hang_off_their_parent()
    {
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        var order = await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);

        var added = await Writer(scoped).AddLinesAsync(
            order.Id,
            [
                Line(world, "Burger") with
                {
                    Modifiers = [Line(world, "Extra cheese", 0.50m)],
                },
                Line(world, "Chips"),
            ],
            null,
            CancellationToken.None);

        Assert.Equal([1, 2, 3], added.Select(l => l.LineNumber));

        var burger = added[0];
        var cheese = added[1];

        Assert.Null(burger.ParentOrderLineId);

        // Not Guid.Empty: there are no navigation properties in this model, so EF does no
        // foreign-key fixup and the parent has to be saved before its children are added.
        Assert.Equal(burger.Id, cheese.ParentOrderLineId);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);
        Assert.Equal(3, await reader.Db.OrderLines.CountAsync());
    }

    [Fact]
    public async Task A_second_round_carries_on_from_the_highest_number_a_void_did_not_reuse()
    {
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        var order = await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);

        var first = await Writer(scoped).AddLinesAsync(
            order.Id, [Line(world, "Soup"), Line(world, "Bread")], null, CancellationToken.None);

        // Somebody changes their mind about the bread.
        var bread = await scoped.Db.OrderLines.SingleAsync(l => l.Id == first[1].Id);
        bread.Status = OrderLineStatus.Voided;
        bread.VoidedAt = DateTimeOffset.UtcNow;
        await scoped.Db.SaveChangesAsync();

        var second = await Writer(scoped).AddLinesAsync(
            order.Id, [Line(world, "Steak")], null, CancellationToken.None);

        // 3, not 2. MAX rather than a count, so a voided line does not hand its number to the
        // next item — "void line 2" has to mean one thing to the person saying it and to the
        // kitchen hearing it, for the whole life of the order.
        Assert.Equal(3, second[0].LineNumber);
    }

    [Fact]
    public async Task Adding_to_a_settled_order_is_refused_under_the_lock()
    {
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        var order = await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);

        var stored = await scoped.Db.Orders.SingleAsync(o => o.Id == order.Id);
        stored.Status = OrderStatus.Closed;
        await scoped.Db.SaveChangesAsync();

        // The restaurant equivalent of ringing a sale into a closed drawer: the money has been
        // counted and a receipt handed over, so the food would be given away with nothing on
        // any row to explain it.
        await Assert.ThrowsAsync<OrderNotOpenException>(
            () => Writer(scoped).AddLinesAsync(
                order.Id, [Line(world, "Coffee")], null, CancellationToken.None));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);
        Assert.Empty(await reader.Db.OrderLines.ToListAsync());
    }

    [Fact]
    public async Task A_failure_inside_the_callback_rolls_the_whole_open_back()
    {
        // Forced rather than hoped for: by the time the callback runs, the order row and its
        // number have both been written. If this were not one transaction the counter would
        // have advanced with no order to show for it — and a gap in a numbered series is
        // exactly what a counter row exists to avoid.
        var world = await ArrangeAsync();

        await using var scoped = Scope(world);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Writer(scoped).OpenAsync(
                TableOrder(world),
                _ => throw new InvalidOperationException("Forced."),
                CancellationToken.None));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        Assert.Empty(await reader.Db.Orders.ToListAsync());
        Assert.Empty(await reader.Db.OrderSequences.ToListAsync());
    }

    [Fact]
    public async Task Two_tills_seating_the_same_table_at_once_produce_one_order()
    {
        // Concurrent for real, per CLAUDE.md invariant 9. A pre-check cannot make this pass:
        // both readers see an empty table and both insert.
        var world = await ArrangeAsync();

        await using var one = Scope(world);
        await using var two = Scope(world);

        var barrier = new Barrier(2);

        async Task<Exception?> SeatAsync(ScopedDbContext scoped)
        {
            await Task.Yield();
            barrier.SignalAndWait();

            try
            {
                await Writer(scoped).OpenAsync(TableOrder(world), null, CancellationToken.None);
                return null;
            }
            catch (Exception caught)
            {
                return caught;
            }
        }

        var outcomes = await Task.WhenAll(SeatAsync(one), SeatAsync(two));

        // Exactly one winner, and the loser lost on the index rather than on a coin toss.
        Assert.Single(outcomes, outcome => outcome is null);
        Assert.Single(outcomes, outcome => outcome is DbUpdateException);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);
        Assert.Single(await reader.Db.Orders.ToListAsync());
    }

    private ScopedDbContext Scope(OrderWorld world) =>
        ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId, world.WaiterId);

    private static OrderWriter Writer(ScopedDbContext scoped) =>
        scoped.Services.GetRequiredService<OrderWriter>();

    private static OrderOpenRequest TableOrder(OrderWorld world) =>
        new(OrderType.Table, world.TableId, null, null, CoverCount: 2, Note: null);

    private static OrderOpenRequest Tab(string name) =>
        new(OrderType.Tab, null, name, null, CoverCount: 1, Note: null);

    private static OrderOpenRequest Takeaway() =>
        new(OrderType.Takeaway, null, null, null, CoverCount: null, Note: null);

    private static OrderLineInstruction Line(OrderWorld world, string description, decimal price = 9.50m) =>
        new(
            world.Catalog.ProductId,
            description,
            Quantity: 1m,
            UnitPrice: (Money)price,
            TaxRate: 0.2300m,
            DiscountAmount: Money.Zero,
            Course: 1,
            SeatNumber: null,
            Note: null);

    private sealed record OrderWorld(Guid TenantId, Guid WaiterId, Guid TableId, SeededCatalog Catalog);

    private async Task<OrderWorld> ArrangeAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var waiterId = Guid.CreateVersion7();

        await using (var owner = ScopedDbContext.WithoutTenant(postgres.ConnectionString))
        {
            owner.Db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Order Writer Tenant",
                Slug = $"t-{tenantId:N}"[..20],
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

        var area = new ServiceArea { Name = "Main room", SortOrder = 1 };
        scoped.Db.ServiceAreas.Add(area);
        await scoped.Db.SaveChangesAsync();

        var table = new DiningTable
        {
            ServiceAreaId = area.Id,
            Name = "4",
            Seats = 4,
            SortOrder = 1,
        };

        scoped.Db.DiningTables.Add(table);
        await scoped.Db.SaveChangesAsync();

        return new OrderWorld(tenantId, waiterId, table.Id, catalog);
    }
}
