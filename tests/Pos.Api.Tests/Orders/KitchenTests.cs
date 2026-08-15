using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Tests.Orders;

/// <summary>
/// Firing a round, routing it, and the screens it lands on.
/// </summary>
/// <remarks>
/// The claim these tests exist for is the one a kitchen would notice being wrong: <b>what was
/// sent stays sent</b>. A ticket is an append-only record of an instruction, so a line voided
/// afterwards is shown against it rather than edited out of it, and a product renamed mid-service
/// does not rewrite what the grill was told at eight.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class KitchenTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_retail_shop_is_refused_by_every_kitchen_route()
    {
        var (client, _) = await factory.TradingTenantAsync();

        using var tickets = await client.GetAsync(new Uri("/api/v1/kitchen/tickets", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, tickets.StatusCode);

        using var stations = await client.GetAsync(new Uri("/api/v1/stations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Conflict, stations.StatusCode);
    }

    [Fact]
    public async Task Each_stations_ticket_holds_only_its_own_items()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var bar = await client.CreateStationAsync("Bar");
        var grill = await client.CreateStationAsync("Grill");

        // Routed on the categories, which is where a restaurant actually configures this. The
        // water is in Grocery; the coffee is in Cheese, which sits under Grocery — so the coffee
        // going to the grill and not the bar is the override being respected one level down.
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, bar);
        await client.RouteCategoryAsync(
            tenant.Catalog.CheeseCategoryId,
            CatalogFixture.CheeseCategoryName,
            grill,
            parent: tenant.Catalog.GroceryCategoryId);

        var orderId = await client.SeatAsync(tenant.TableId);

        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);
        await client.AddLineAsync(orderId, tenant.Catalog.CoffeeProductId);

        var tickets = await client.FireAsync(orderId, course: 1);

        Assert.Equal(2, tickets.GetArrayLength());

        var barTicket = tickets.EnumerateArray().Single(t => t.GetProperty("stationName").GetString() == "Bar");
        var grillTicket = tickets.EnumerateArray().Single(t => t.GetProperty("stationName").GetString() == "Grill");

        Assert.Equal(
            CatalogFixture.WaterName,
            barTicket.GetProperty("lines").EnumerateArray().Single().GetProperty("description").GetString());

        Assert.Equal(
            CatalogFixture.CoffeeName,
            grillTicket.GetProperty("lines").EnumerateArray().Single().GetProperty("description").GetString());

        // And the label the kitchen reads is the table, not an id.
        Assert.Equal("Table 4", barTicket.GetProperty("orderLabel").GetString());
    }

    [Fact]
    public async Task Firing_twice_does_not_cook_the_round_twice()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var first = await client.FireAsync(orderId, course: 1);
        Assert.Equal(1, first.GetArrayLength());

        // A different key, which is the case a stored response would not cover: the second
        // handheld tapping fire a beat later mints its own. What stops the double is the line's
        // status, changed in the same transaction that wrote the first ticket.
        var second = await client.FireAsync(orderId, course: 1);
        Assert.Equal(0, second.GetArrayLength());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Equal(1, await db.KitchenTickets.CountAsync(t => t.OrderId == orderId));
            Assert.Equal(1, await db.KitchenTicketLines.CountAsync());
        });
    }

    [Fact]
    public async Task A_replay_under_the_same_key_returns_the_original_tickets()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var key = Guid.CreateVersion7();

        using var first = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/fire", new { course = 1 }, key);
        using var replay = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/fire", new { course = 1 }, key);

        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        var original = await first.Content.ReadFromJsonAsync<JsonElement>();
        var replayed = await replay.Content.ReadFromJsonAsync<JsonElement>();

        // The original tickets, not the empty list a re-execution would produce. A waiter whose
        // network dropped needs to be told what went to the kitchen, not "nothing to fire".
        Assert.Equal(
            original.EnumerateArray().Single().GetProperty("id").GetGuid(),
            replayed.EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Only_the_named_course_goes()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);

        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, course: 1);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, course: 2);

        var starters = await client.FireAsync(orderId, course: 1);

        var ticket = starters.EnumerateArray().Single();
        Assert.Equal(1, ticket.GetProperty("course").GetInt32());
        Assert.Equal(1, ticket.GetProperty("lines").GetArrayLength());

        // The main is still in the waiter's hands, which is the whole point of coursing.
        var mains = await client.FireAsync(orderId, course: 2);
        Assert.Equal(2, mains.EnumerateArray().Single().GetProperty("course").GetInt32());
    }

    [Fact]
    public async Task Something_with_no_station_refuses_the_whole_fire_and_names_it()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);

        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        // The carrier bag has no category at all, so nothing routes it anywhere.
        await client.AddLineAsync(orderId, tenant.Catalog.BagProductId);

        using var response = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/fire", new { course = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("https://pos.example/errors/product-not-routed", problem.GetProperty("type").GetString());

        // Named, because a manager fixing this needs to know which item — and the water, which
        // was routed, is not mentioned.
        Assert.Contains(CatalogFixture.BagName, problem.GetProperty("detail").GetString(), StringComparison.Ordinal);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Nothing was written and nothing was fired. The retry after the menu is fixed is
            // the same request, which is the reason for refusing the round rather than the line.
            Assert.Empty(await db.KitchenTickets.ToListAsync());
            Assert.Equal(2, await db.OrderLines.CountAsync(l => l.Status == OrderLineStatus.Pending));
        });
    }

    [Fact]
    public async Task A_modifier_rides_its_parent_as_text_rather_than_a_line_of_its_own()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var extra = await client.CreateModifierProductAsync("Extra shot", tenant.Catalog.StandardTaxClassId);

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
                        course = 1,
                        modifiers = new[] { new { productId = extra, quantity = 1m } },
                    },
                },
            });

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var tickets = await client.FireAsync(orderId, course: 1);

        var line = tickets.EnumerateArray().Single().GetProperty("lines").EnumerateArray().Single();

        Assert.Equal("Extra shot", line.GetProperty("modifierText").GetString());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // One ticket line for two order lines: the modifier is a phrase, not a row.
            Assert.Equal(1, await db.KitchenTicketLines.CountAsync());

            // And it went with its parent rather than being left behind to fire on its own.
            Assert.Equal(2, await db.OrderLines.CountAsync(l => l.Status == OrderLineStatus.Fired));
        });
    }

    [Fact]
    public async Task A_voided_line_is_struck_through_rather_than_removed_from_the_ticket()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);
        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);
        var lineId = order.GetProperty("lines").EnumerateArray().Single().GetProperty("id").GetGuid();

        await client.FireAsync(orderId, course: 1);

        using var voided = await client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/lines/{lineId}/void",
            new { reason = "Sent back" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var tickets = await client.TicketsAsync();

        var line = tickets.EnumerateArray().Single().GetProperty("lines").EnumerateArray().Single();

        // Still there, and marked. The chef may already have plated it, and a line that vanished
        // would erase the evidence that the shop lost one.
        Assert.True(line.GetProperty("isVoided").GetBoolean());
        Assert.Equal(CatalogFixture.WaterName, line.GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_bumped_ticket_leaves_the_screen_and_a_recall_puts_it_back()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var ticketId = (await client.FireAsync(orderId, course: 1))
            .EnumerateArray().Single().GetProperty("id").GetGuid();

        using var bumped = await client.PostAsJsonAsync($"/api/v1/kitchen/tickets/{ticketId}/bump", new { });
        Assert.Equal(HttpStatusCode.OK, bumped.StatusCode);

        Assert.Equal(0, (await client.TicketsAsync()).GetArrayLength());
        Assert.Equal(1, (await client.TicketsAsync(includeBumped: true)).GetArrayLength());

        // Bumping it again is not a conflict. Two chefs reaching for one screen is ordinary, and
        // the outcome is what both of them wanted.
        using var again = await client.PostAsJsonAsync($"/api/v1/kitchen/tickets/{ticketId}/bump", new { });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        using var recalled = await client.PostAsJsonAsync($"/api/v1/kitchen/tickets/{ticketId}/recall", new { });
        Assert.Equal(HttpStatusCode.OK, recalled.StatusCode);

        var back = (await client.TicketsAsync()).EnumerateArray().Single();

        Assert.Equal(ticketId, back.GetProperty("id").GetGuid());
        Assert.Equal(nameof(KitchenTicketStatus.Active), back.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, back.GetProperty("bumpedAt").ValueKind);
    }

    [Fact]
    public async Task A_ticket_keeps_what_it_said_when_the_product_is_renamed()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        await client.FireAsync(orderId, course: 1);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.FirstAsync(p => p.Id == tenant.Catalog.WaterProductId);

            product.Name = "Sparkling Water 500ml";
            await db.SaveChangesAsync();
        });

        var tickets = await client.TicketsAsync();

        Assert.Equal(
            CatalogFixture.WaterName,
            tickets.EnumerateArray().Single()
                .GetProperty("lines").EnumerateArray().Single()
                .GetProperty("description").GetString());
    }

    [Fact]
    public async Task A_settled_order_cannot_be_fired()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var pass = await client.CreateStationAsync("Pass");
        await client.RouteCategoryAsync(tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        using var abandoned = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/abandon",
            new { reason = "Walked out" });

        Assert.Equal(HttpStatusCode.OK, abandoned.StatusCode);

        using var response = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/fire", new { course = 1 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://pos.example/errors/order-not-open", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_station_name_has_to_be_unique_in_the_shop()
    {
        var (client, _) = await factory.RestaurantTenantAsync();

        await client.CreateStationAsync("Grill");

        using var duplicate = await client.PostAsJsonAsync("/api/v1/stations", new { name = "Grill" });

        Assert.Equal(HttpStatusCode.BadRequest, duplicate.StatusCode);

        var problem = await duplicate.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(problem.GetProperty("errors").TryGetProperty("name", out _));
    }

    [Fact]
    public async Task A_cashier_can_work_the_kitchen_but_not_rearrange_it()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        await client.CreateStationAsync("Pass");

        var cashier = await factory.CashierClientAsync(tenant);

        using var read = await cashier.GetAsync(new Uri("/api/v1/kitchen/tickets", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);

        using var stations = await cashier.GetAsync(new Uri("/api/v1/stations", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, stations.StatusCode);

        // Changing what the stations are re-routes the menu, which is CanManageFloor.
        using var created = await cashier.PostAsJsonAsync("/api/v1/stations", new { name = "Fryer" });
        Assert.Equal(HttpStatusCode.Forbidden, created.StatusCode);
    }
}

/// <summary>
/// The arranging a kitchen test does — a station, a routed category, a fire, a poll.
/// </summary>
/// <remarks>
/// Its own class because C# will not put an extension method on a test class, and extensions
/// because these read as the actions a person takes: create a station, route a category, fire.
/// </remarks>
internal static class KitchenTestExtensions
{
    public static async Task<Guid> CreateStationAsync(this HttpClient client, string name)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/stations", new { name });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Points a category at a station through the real endpoint.</summary>
    /// <remarks>
    /// The name and the parent have to be sent back: <c>PUT /categories/{id}</c> replaces and
    /// there is no PATCH, so omitting the parent would flatten Cheese out from under Grocery —
    /// which is the hierarchy the routing walk is being tested against.
    /// </remarks>
    public static async Task RouteCategoryAsync(
        this HttpClient client,
        Guid categoryId,
        string name,
        Guid stationId,
        Guid? parent = null)
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/v1/categories/{categoryId}",
            new { name, parentCategoryId = parent, stationId });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static async Task<Guid> CreateModifierProductAsync(
        this HttpClient client,
        string name,
        Guid taxClassId)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/products",
            new
            {
                sku = $"MOD-{Guid.CreateVersion7():N}"[..12],
                name,
                taxClassId,
                unitPrice = 0.50m,
                trackStock = false,
                isModifier = true,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    public static async Task<JsonElement> FireAsync(this HttpClient client, Guid orderId, int? course = null)
    {
        using var response = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/fire", new { course });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public static async Task<JsonElement> TicketsAsync(this HttpClient client, bool includeBumped = false)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/kitchen/tickets?includeBumped={includeBumped}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
