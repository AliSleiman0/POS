using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Orders;

/// <summary>
/// Seating, ordering, amending, moving and giving up — the floor's whole day.
/// </summary>
[Collection(PosApiCollection.Name)]
public sealed class OrderLifecycleTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_retail_shop_is_refused_by_every_order_route()
    {
        // The gate, from the outside. A 409 and not a 403: an owner holds every policy there
        // is, and the route exists — what is wrong is the shop's state, and it is one the
        // caller can change.
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = nameof(OrderType.Takeaway) });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "https://pos.example/errors/restaurant-mode-required",
            problem.GetProperty("type").GetString());

        // And the read side too, so a client cannot conclude the shop simply has no tables.
        using var floor = await client.GetAsync(new Uri("/api/v1/floor", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, floor.StatusCode);
    }

    [Fact]
    public async Task A_shop_cannot_switch_back_to_retail_with_tables_still_open()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        await client.SeatAsync(tenant.TableId);

        using var refused = await client.PutAsJsonAsync("/api/v1/settings", Settings(nameof(ServiceMode.Retail)));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "https://pos.example/errors/orders-still-open",
            problem.GetProperty("type").GetString());

        // Nothing historical is at risk here — the guard is about work in progress. Settling or
        // abandoning the table is the real next move, so it is not a permanent refusal.
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shop = await db.Tenants.FirstAsync(t => t.Id == tenant.TenantId);

            Assert.Equal(ServiceMode.Restaurant, shop.ServiceMode);
        });
    }

    [Fact]
    public async Task A_cross_tenant_table_cannot_be_seated()
    {
        // The exemption row in IsolationManifest names this test. The diningTableId travels in
        // the body, so the answer is 400 on the field — identical whether the table is unknown,
        // retired, or another shop's, which is what stops it being an existence oracle.
        var (_, victim) = await factory.RestaurantTenantAsync();
        var (client, _) = await factory.RestaurantTenantAsync();

        using var response = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = nameof(OrderType.Table), diningTableId = victim.TableId, coverCount = 2 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("diningTableId", out _));

        // And the victim gained nothing. A 400 that had already written a row would be worse
        // than a 200.
        await factory.AsTenantAsync(victim.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Orders.ToListAsync());
        });
    }

    [Fact]
    public async Task A_table_cannot_carry_two_open_orders()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        await client.SeatAsync(tenant.TableId);

        using var second = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = nameof(OrderType.Table), diningTableId = tenant.TableId, coverCount = 4 });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var problem = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "https://pos.example/errors/table-already-occupied",
            problem.GetProperty("type").GetString());

        // The next round of drinks has to join the bill that is already there. Two would mean
        // one of them gets paid and the other does not.
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Single(await db.Orders.Where(o => o.Status == OrderStatus.Open).ToListAsync());
        });
    }

    [Fact]
    public async Task A_tab_needs_a_name_and_a_table_order_needs_a_table()
    {
        var (client, _) = await factory.RestaurantTenantAsync();

        using var namelessTab = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = nameof(OrderType.Tab) });

        Assert.Equal(HttpStatusCode.BadRequest, namelessTab.StatusCode);

        var tabProblem = await namelessTab.Content.ReadFromJsonAsync<JsonElement>();

        // Required, because a tab with no name cannot be found again — which is the entire
        // purpose of a tab.
        Assert.True(tabProblem.GetProperty("errors").TryGetProperty("tabName", out _));

        using var tablelessTable = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = nameof(OrderType.Table) });

        Assert.Equal(HttpStatusCode.BadRequest, tablelessTable.StatusCode);
    }

    [Fact]
    public async Task Ordering_snapshots_the_price_and_the_rate_as_of_now()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var line = order.GetProperty("lines")[0];
        var orderedPrice = line.GetProperty("unitPrice").GetDecimal();

        // The menu changes underneath, as it does at 19:00 in a real shop.
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.FirstAsync(p => p.Id == tenant.Catalog.WaterProductId);

            product.UnitPrice = (Core.Monetary.Money)99m;
            await db.SaveChangesAsync();
        });

        using var reread = await client.GetAsync(new Uri($"/api/v1/orders/{orderId}", UriKind.Relative));
        var after = await reread.Content.ReadFromJsonAsync<JsonElement>();

        // The guest pays what they were told when they ordered. This is CLAUDE.md invariant 5
        // applied one step earlier than a sale line, and it is why an order line carries its own
        // price rather than joining to the catalog.
        Assert.Equal(orderedPrice, after.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
        Assert.NotEqual(99m, orderedPrice);
    }

    [Fact]
    public async Task A_fired_line_cannot_be_amended_and_a_pending_one_can()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();

        using var amended = await client.PatchAsJsonAsync(
            $"/api/v1/orders/{orderId}/lines/{lineId}",
            new { quantity = 3m });

        Assert.Equal(HttpStatusCode.OK, amended.StatusCode);
        Assert.Equal(3m, (await amended.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("quantity").GetDecimal());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var line = await db.OrderLines.FirstAsync(l => l.Id == lineId);

            line.Status = OrderLineStatus.Fired;
            line.FiredAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
        });

        using var refused = await client.PatchAsJsonAsync(
            $"/api/v1/orders/{orderId}/lines/{lineId}",
            new { quantity = 9m });

        // The kitchen is cooking what it was told. Silently changing the quantity underneath
        // would bill for three of something two of which exist.
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var line = await db.OrderLines.FirstAsync(l => l.Id == lineId);

            Assert.Equal(3m, line.Quantity);
        });
    }

    [Fact]
    public async Task Voiding_a_line_takes_its_modifiers_with_it()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        using var added = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new
            {
                lines = new[]
                {
                    new
                    {
                        productId = tenant.Catalog.WaterProductId,
                        quantity = 1m,
                        modifiers = new[] { new { productId = tenant.Catalog.BagProductId, quantity = 1m } },
                    },
                },
            });

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var order = await added.Content.ReadFromJsonAsync<JsonElement>();
        var lines = order.GetProperty("lines");

        Assert.Equal(2, lines.GetArrayLength());

        var parentId = lines[0].GetProperty("id").GetGuid();
        Assert.Equal(parentId, lines[1].GetProperty("parentOrderLineId").GetGuid());

        using var voided = await client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/lines/{parentId}/void",
            new { reason = "Changed their mind" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var all = await db.OrderLines.Where(l => l.OrderId == orderId).ToListAsync();

            // "No cheese" on a burger nobody is having is not something the kitchen or the bill
            // should still be carrying.
            Assert.All(all, line => Assert.Equal(OrderLineStatus.Voided, line.Status));
        });
    }

    [Fact]
    public async Task A_modifier_cannot_itself_have_modifiers()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new
            {
                lines = new[]
                {
                    new
                    {
                        productId = tenant.Catalog.WaterProductId,
                        modifiers = new[]
                        {
                            new
                            {
                                productId = tenant.Catalog.BagProductId,
                                modifiers = new[] { new { productId = tenant.Catalog.CoffeeProductId } },
                            },
                        },
                    },
                },
            });

        // One level deep, by rule. A modifier of a modifier is a menu that needs rethinking
        // rather than a data structure that needs recursion, and a kitchen ticket has nowhere
        // to print the third level.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_cannot_apply_a_discount_but_an_owner_can()
    {
        var (owner, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await owner.SeatAsync(tenant.TableId);

        using var cashier = await factory.CashierClientAsync(tenant);

        using var refused = await cashier.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new
            {
                lines = new[]
                {
                    new { productId = tenant.Catalog.WaterProductId, quantity = 1m, discountAmount = 0.50m },
                },
            });

        // Refused rather than silently dropped: quietly ignoring it would charge the customer
        // the menu price after somebody told them otherwise.
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.OrderLines.Where(l => l.OrderId == orderId).ToListAsync());
        });

        using var allowed = await owner.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new
            {
                lines = new[]
                {
                    new { productId = tenant.Catalog.WaterProductId, quantity = 1m, discountAmount = 0.50m },
                },
            });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
    }

    [Fact]
    public async Task Merging_moves_the_lines_and_leaves_the_numbers_unique()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var target = await client.SeatAsync(tenant.TableId);
        var source = await client.SeatAsync(tenant.SecondTableId);

        await client.AddLineAsync(target, tenant.Catalog.WaterProductId);
        await client.AddLineAsync(source, tenant.Catalog.CoffeeProductId);
        await client.AddLineAsync(source, tenant.Catalog.BagProductId);

        using var merged = await client.PostIdempotentAsync(
            $"/api/v1/orders/{target}/merge",
            new { sourceOrderId = source });

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        var order = await merged.Content.ReadFromJsonAsync<JsonElement>();
        var numbers = order.GetProperty("lines").EnumerateArray()
            .Select(l => l.GetProperty("lineNumber").GetInt32())
            .ToArray();

        // Renumbered on arrival, so "void line 2" still means one thing. Two orders both
        // holding a line 1 would otherwise collide the moment they were combined.
        Assert.Equal([1, 2, 3], numbers);

        // And the source table is free again, because its order is closed.
        using var open = await client.GetAsync(new Uri("/api/v1/orders", UriKind.Relative));
        var stillOpen = await open.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Single(stillOpen.EnumerateArray());
    }

    [Fact]
    public async Task Abandoning_needs_a_reason()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/abandon",
            new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task Seating_twice_with_one_key_seats_one_table()
    {
        // Invariant 6, at a till somebody double-tapped. The second press must return the first
        // order rather than colliding on the table index and reading as a failure.
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var key = Guid.CreateVersion7();
        var body = new { type = nameof(OrderType.Table), diningTableId = tenant.TableId, coverCount = 2 };

        using var first = await client.PostIdempotentAsync("/api/v1/orders", body, key);
        using var second = await client.PostIdempotentAsync("/api/v1/orders", body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var one = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var two = (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        Assert.Equal(one, two);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Single(await db.Orders.ToListAsync());
        });
    }

    private static Dictionary<string, object?> Settings(string serviceMode) =>
        new(StringComparer.Ordinal)
        {
            ["name"] = "The Corner Table",
            ["taxMode"] = nameof(TaxMode.Inclusive),
            ["serviceMode"] = serviceMode,
            ["cashRoundingIncrement"] = 0m,
            ["addressLine"] = null,
            ["taxNumber"] = null,
            ["receiptHeader"] = null,
            ["receiptFooter"] = null,
        };
}

/// <summary>The room: creating it, and not creating it in somebody else's shop.</summary>
[Collection(PosApiCollection.Name)]
public sealed class FloorTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_cross_tenant_area_cannot_have_a_table_added_to_it()
    {
        // Named by the exemption row in IsolationManifest. The serviceAreaId travels in the
        // body, so the answer is 400 on the field — identical whether the area is unknown or
        // another shop's, which is what stops it being an existence oracle.
        var (_, victim) = await factory.RestaurantTenantAsync();
        var (client, _) = await factory.RestaurantTenantAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/floor/tables",
            new { serviceAreaId = victim.AreaId, name = "Stolen", seats = 4 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("serviceAreaId", out _));

        await factory.AsTenantAsync(victim.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Two, from the fixture. A third would mean the write landed in the wrong shop.
            Assert.Equal(2, await db.DiningTables.CountAsync());
        });
    }

    [Fact]
    public async Task Two_tables_cannot_share_a_name()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/floor/tables",
            new { serviceAreaId = tenant.AreaId, name = "4", seats = 2 });

        // "Table 4" has to mean one table. Two of them is a floor nobody can work, and a
        // transfer naming one by its label would pick arbitrarily between them.
        //
        // The unique index is the authority — two people adding "Table 4" at the same moment
        // both pass a pre-check — but a manager building a floor plan is told the name is
        // taken rather than shown an unexplained failure.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task A_cashier_can_read_the_room_but_not_change_it()
    {
        var (_, tenant) = await factory.RestaurantTenantAsync();

        using var cashier = await factory.CashierClientAsync(tenant);

        using var read = await cashier.GetAsync(new Uri("/api/v1/floor", UriKind.Relative));
        using var written = await cashier.PostAsJsonAsync(
            "/api/v1/floor/areas",
            new { name = "Terrace" });

        // Everybody working the room has to see it; renaming a table changes what every
        // historical report about it says.
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, written.StatusCode);
    }

    [Fact]
    public async Task The_floor_reads_back_as_areas_holding_their_tables()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        using var response = await client.GetAsync(new Uri("/api/v1/floor", UriKind.Relative));
        var areas = await response.Content.ReadFromJsonAsync<JsonElement>();

        var area = Assert.Single(areas.EnumerateArray());

        Assert.Equal(tenant.AreaId, area.GetProperty("id").GetGuid());
        Assert.Equal(2, area.GetProperty("tables").GetArrayLength());
    }
}
