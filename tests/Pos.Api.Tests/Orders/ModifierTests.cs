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
/// Modifiers: the menu's questions, and what happens when a line does not answer them.
/// </summary>
/// <remarks>
/// The rule itself is unit-tested in <c>ModifierRulesTests</c> without a database. What these
/// prove is that it is <b>reached</b> — a pure rule nobody calls is the same as no rule, and the
/// sheet's own gating is explicitly a courtesy.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ModifierTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_required_group_refuses_a_line_that_does_not_answer_it()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var groupId = await CreateGroupAsync(client, tenant, min: 1, max: 1);
        await AssignAsync(client, tenant.Catalog.WaterProductId, groupId);

        var orderId = await client.SeatAsync(tenant.TableId);

        using var refused = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new { lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        var errors = problem.GetProperty("errors");

        Assert.True(errors.TryGetProperty("lines[0].modifiers", out var messages));
        Assert.Contains("How would you like it?", messages[0].GetString(), StringComparison.Ordinal);

        // Nothing was written. A 400 that had already put the line on the order would send an
        // unanswered steak to the grill anyway.
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.OrderLines.Where(l => l.OrderId == orderId).ToListAsync());
        });
    }

    [Fact]
    public async Task Answering_the_question_puts_the_modifier_on_as_a_child_line()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var groupId = await CreateGroupAsync(client, tenant, min: 1, max: 1);
        await AssignAsync(client, tenant.Catalog.WaterProductId, groupId);

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

        // The modifier is an ordinary line with its own price and its own tax rate, which is
        // the whole reason a modifier is a product: it prices through the same engine as
        // everything else, and the bill can itemise it.
        var modifier = lines[1];

        Assert.Equal(lines[0].GetProperty("id").GetGuid(), modifier.GetProperty("parentOrderLineId").GetGuid());
        Assert.True(modifier.GetProperty("unitPrice").GetDecimal() >= 0m);
        Assert.True(modifier.GetProperty("taxRate").GetDecimal() >= 0m);
    }

    [Fact]
    public async Task An_option_the_item_does_not_offer_is_refused()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var groupId = await CreateGroupAsync(client, tenant, min: 0, max: 1);
        await AssignAsync(client, tenant.Catalog.WaterProductId, groupId);

        var orderId = await client.SeatAsync(tenant.TableId);

        using var refused = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new
            {
                lines = new[]
                {
                    new
                    {
                        productId = tenant.Catalog.WaterProductId,
                        quantity = 1m,

                        // Coffee is not in the group. Dropping it silently would charge for
                        // something the kitchen never heard about.
                        modifiers = new[] { new { productId = tenant.Catalog.CoffeeProductId, quantity = 1m } },
                    },
                },
            });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task A_product_with_no_groups_is_unaffected()
    {
        // Every retail product, and most of a restaurant's. The check reads nothing when the
        // batch touches no groups, so a round of beers costs no extra query.
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        var order = await client.AddLineAsync(orderId, tenant.Catalog.CoffeeProductId);

        Assert.Equal(1, order.GetProperty("lines").GetArrayLength());
    }

    [Fact]
    public async Task Modifiers_are_kept_out_of_the_product_list_unless_asked_for()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var bag = await db.Products.FirstAsync(p => p.Id == tenant.Catalog.BagProductId);

            bag.IsModifier = true;
            await db.SaveChangesAsync();
        });

        using var defaultList = await client.GetAsync(new Uri("/api/v1/products", UriKind.Relative));
        var without = await defaultList.Content.ReadFromJsonAsync<JsonElement>();

        // Nobody scans "extra cheese", and a till that offered it as a line item would sell one.
        Assert.DoesNotContain(
            without.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == tenant.Catalog.BagProductId);

        using var withThem = await client.GetAsync(
            new Uri("/api/v1/products?includeModifiers=true", UriKind.Relative));

        var with = await withThem.Content.ReadFromJsonAsync<JsonElement>();

        // The catalog's modifier screen asks for them explicitly.
        Assert.Contains(
            with.GetProperty("items").EnumerateArray(),
            item => item.GetProperty("id").GetGuid() == tenant.Catalog.BagProductId);
    }

    [Fact]
    public async Task The_offline_mirror_is_told_which_products_are_modifiers()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var bag = await db.Products.FirstAsync(p => p.Id == tenant.Catalog.BagProductId);

            bag.IsModifier = true;
            await db.SaveChangesAsync();
        });

        using var response = await client.GetAsync(new Uri("/api/v1/catalog/sync", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var mirrored = body.GetProperty("products").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == tenant.Catalog.BagProductId);

        // A till that offered "extra cheese" as a scannable line while the network was down
        // would sell one, and the sale would be perfectly valid.
        Assert.True(mirrored.GetProperty("isModifier").GetBoolean());
    }

    [Fact]
    public async Task A_group_whose_maximum_is_below_its_minimum_is_refused()
    {
        var (client, _) = await factory.RestaurantTenantAsync();

        using var response = await client.PostAsJsonAsync(
            "/api/v1/menu/modifier-groups",
            new { name = "Impossible", minSelections = 2, maxSelections = 1 });

        // A group nothing can satisfy reads at the till as an item that cannot be ordered,
        // with no message that says why.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("maxSelections", out _));
    }

    [Fact]
    public async Task A_cashier_can_read_the_menu_but_not_change_it()
    {
        var (_, tenant) = await factory.RestaurantTenantAsync();

        using var cashier = await factory.CashierClientAsync(tenant);

        using var read = await cashier.GetAsync(new Uri("/api/v1/menu/modifier-groups", UriKind.Relative));
        using var written = await cashier.PostAsJsonAsync(
            "/api/v1/menu/modifier-groups",
            new { name = "Cooked how?", minSelections = 1, maxSelections = 1 });

        // Every order screen reads this; changing what a question offers is the same authority
        // as changing a price.
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, written.StatusCode);
    }

    [Fact]
    public async Task The_questions_an_item_asks_come_back_in_the_order_they_were_assigned()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var first = await CreateGroupAsync(client, tenant, min: 1, max: 1, name: "How would you like it?");
        var second = await CreateGroupAsync(client, tenant, min: 0, max: null, name: "Any sides?");

        using var assigned = await client.PutAsJsonAsync(
            $"/api/v1/menu/products/{tenant.Catalog.WaterProductId}/modifier-groups",
            new { modifierGroupIds = new[] { second, first } });

        Assert.Equal(HttpStatusCode.OK, assigned.StatusCode);

        var groups = await assigned.Content.ReadFromJsonAsync<JsonElement>();

        // The order lives on the pairing, not on the group: "Cooked how?" comes before
        // "Any sides?" on a steak and might not on something else.
        Assert.Equal(second, groups[0].GetProperty("id").GetGuid());
        Assert.Equal(first, groups[1].GetProperty("id").GetGuid());
    }

    private static async Task<Guid> CreateGroupAsync(
        HttpClient client,
        RestaurantTenant tenant,
        int min,
        int? max,
        string name = "How would you like it?")
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/menu/modifier-groups",
            new
            {
                name,
                minSelections = min,
                maxSelections = max,
                optionProductIds = new[] { tenant.Catalog.BagProductId },
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssignAsync(HttpClient client, Guid productId, Guid groupId)
    {
        using var response = await client.PutAsJsonAsync(
            $"/api/v1/menu/products/{productId}/modifier-groups",
            new { modifierGroupIds = new[] { groupId } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
