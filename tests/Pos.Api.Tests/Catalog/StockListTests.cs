using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// <c>GET /stock</c> — what is on the shelf, and what needs ordering.
/// </summary>
/// <remarks>
/// <b>Each test gets its own tenant.</b> The assertions here are about which products appear
/// and which do not, and against the shared sandbox "does this list contain exactly these
/// three?" answers "did any other test create a stocked product?" instead.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class StockListTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/stock";

    [Fact]
    public async Task A_product_that_has_never_been_counted_reads_zero_rather_than_vanishing()
    {
        // The list is driven off products, not stock rows, precisely for this. A join would
        // drop the uncounted products, and those are the ones most worth seeing.
        var shop = await NewShopAsync();

        var productId = await shop.CreateProductAsync("Uncounted");

        var row = await shop.FindAsync(productId);

        Assert.Equal(0m, row.GetProperty("onHand").GetDecimal());
        Assert.Equal(JsonValueKind.Null, row.GetProperty("reorderPoint").ValueKind);
        Assert.False(row.GetProperty("belowReorderPoint").GetBoolean());
    }

    /// <remarks>
    /// <b>This is the test that was missing, and its absence shipped a search box that did
    /// nothing.</b> The web client has sent <c>?q=</c> to this endpoint since Phase 4.3; the
    /// handler never declared the parameter, so ASP.NET dropped it and the stock screen returned
    /// an unfiltered first page. It looked correct for as long as the shop had fewer products
    /// than a page — which the e2e database did, until it did not, and the catalog spec went red
    /// on a row that was simply on page two.
    /// <para>
    /// Every existing test here reads <c>?limit=200</c> and picks its product out of the body,
    /// so not one of them ever passed a term. A list endpoint's filters need a test each.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task The_search_term_filters_the_list_rather_than_being_ignored()
    {
        var shop = await NewShopAsync();

        var wanted = await shop.CreateProductAsync("Marmalade Thick Cut");
        await shop.CreateProductAsync("Blackcurrant Jam");

        var found = await shop.SearchAsync("marmalade");

        Assert.Contains(found, id => id == wanted);
        Assert.Single(found);
    }

    [Fact]
    public async Task A_wildcard_in_the_term_matches_nothing_rather_than_everything()
    {
        // The escaping, asserted from the outside. Unescaped, "%" is a pattern that matches
        // every product in the shop — so a search box would look like it worked while doing the
        // opposite of filtering.
        var shop = await NewShopAsync();

        await shop.CreateProductAsync("Marmalade Thick Cut");

        Assert.Empty(await shop.SearchAsync("%"));
    }

    [Fact]
    public async Task A_stock_search_finds_the_same_product_the_catalog_search_does()
    {
        // One definition of "matches", shared. Two would drift, and a shopkeeper would learn
        // that the stock screen cannot find something the catalog screen can.
        var shop = await NewShopAsync();

        var productId = await shop.CreateProductAsync("Marmalade Thick Cut");

        var catalog = await shop.Client.GetAsync(
            new Uri("/api/v1/products?q=thick", UriKind.Relative));

        var listed = (await catalog.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("id").GetGuid())
            .ToArray();

        Assert.Equal(listed, await shop.SearchAsync("thick"));
        Assert.Contains(productId, listed);
    }

    [Fact]
    public async Task On_hand_follows_the_adjustments()
    {
        var shop = await NewShopAsync();

        var productId = await shop.CreateProductAsync("Counted");

        await shop.AdjustAsync(productId, "Receive", 9m, "Delivery");
        await shop.AdjustAsync(productId, "Waste", -2m, "Damaged");

        Assert.Equal(7.0000m, (await shop.FindAsync(productId)).GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task A_product_that_does_not_track_stock_is_not_in_the_list()
    {
        // A carrier bag with an on-hand of zero is noise in a report whose job is to show
        // what needs ordering.
        var shop = await NewShopAsync();

        var tracked = await shop.CreateProductAsync("Tracked");
        var untracked = await shop.CreateProductAsync("Untracked", trackStock: false);

        var ids = await shop.IdsAsync();

        Assert.Contains(tracked, ids);
        Assert.DoesNotContain(untracked, ids);
    }

    [Fact]
    public async Task Below_reorder_point_returns_the_products_that_need_ordering_and_no_others()
    {
        var shop = await NewShopAsync();

        var low = await shop.CreateProductAsync("Low");
        var plenty = await shop.CreateProductAsync("Plenty");

        await shop.AdjustAsync(low, "Receive", 2m, "Delivery");
        await shop.AdjustAsync(plenty, "Receive", 50m, "Delivery");

        // The reorder point is set on the stock row, which only the ledger creates — so it is
        // set here through the database rather than through an endpoint that does not exist
        // yet. Phase 4's catalog UI is what will need one.
        await shop.SetReorderPointAsync(low, 5m);
        await shop.SetReorderPointAsync(plenty, 5m);

        var ids = await shop.IdsAsync("?belowReorderPoint=true");

        // Both halves. A filter that returned everything would pass the first assertion and
        // a filter that returned nothing would pass the second.
        Assert.Contains(low, ids);
        Assert.DoesNotContain(plenty, ids);
    }

    [Fact]
    public async Task A_product_with_no_reorder_point_never_needs_ordering()
    {
        // Otherwise every uncounted product — on-hand zero, no point set — would appear in
        // the reorder report on the day it was created, and the report would be ignored.
        var shop = await NewShopAsync();

        var productId = await shop.CreateProductAsync("No point set");

        Assert.DoesNotContain(productId, await shop.IdsAsync("?belowReorderPoint=true"));
    }

    [Fact]
    public async Task A_deactivated_product_leaves_the_list_and_can_be_asked_for_again()
    {
        var shop = await NewShopAsync();

        var productId = await shop.CreateProductAsync("Withdrawn");

        await shop.DeactivateAsync(productId);

        Assert.DoesNotContain(productId, await shop.IdsAsync());

        // Still countable, though: stock that exists does not stop existing because the
        // product was withdrawn, and somebody has to write it off.
        Assert.Contains(productId, await shop.IdsAsync("?activeOnly=false"));
    }

    [Fact]
    public async Task A_cashier_may_read_the_levels()
    {
        // CanSell, not CanManageCatalog. "Have we got any more out the back?" is asked at the
        // till, by the person standing at it.
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(new Uri(Route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData("?limit=0")]
    [InlineData("?limit=201")]
    [InlineData("?cursor=not-a-cursor")]
    public async Task A_bad_page_request_is_refused_rather_than_clamped(string query)
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(new Uri(Route + query, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    private async Task<Shop> NewShopAsync()
    {
        var slug = $"stock-{Guid.CreateVersion7():N}"[..20];
        const string Email = "owner@stock.test";

        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");

        await factory.CreateUserAsync(tenant.Id, Email, CatalogSandbox.Password, RoleNames.Owner);

        var catalog = await CatalogFixture.WriteAsync(factory, tenant.Id);

        var client = factory.CreateClient();

        client.WithBearer(
            (await client.LoginAsync(slug, Email, CatalogSandbox.Password)).AccessToken);

        return new Shop(factory, client, tenant.Id, catalog);
    }

    /// <summary>One throwaway tenant and the handful of calls these tests make against it.</summary>
    private sealed record Shop(
        PosApiFactory Factory,
        HttpClient Client,
        Guid TenantId,
        SeededCatalog Catalog)
    {
        public async Task<Guid> CreateProductAsync(string name, bool trackStock = true)
        {
            var response = await Client.PostAsJsonAsync("/api/v1/products", new
            {
                sku = $"LVL-{Guid.CreateVersion7():N}"[..20],
                name,
                unitPrice = 1.0000m,
                taxClassId = Catalog.StandardTaxClassId,
                trackStock,
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        public async Task AdjustAsync(Guid productId, string type, decimal quantity, string reason)
        {
            var response = await Client.PostIdempotentAsync(
                "/api/v1/stock/adjustments",
                new { productId, type, quantity, reason });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        public async Task DeactivateAsync(Guid productId)
        {
            var response = await Client.PostAsJsonAsync($"/api/v1/products/{productId}/deactivate", new { });

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        public Task SetReorderPointAsync(Guid productId, decimal reorderPoint) =>
            Factory.AsTenantAsync(TenantId, async services =>
            {
                var db = services.GetRequiredService<AppDbContext>();

                var stock = await db.StockItems.FirstAsync(s => s.ProductId == productId);

                stock.ReorderPoint = reorderPoint;
                await db.SaveChangesAsync();
            });

        public async Task<IReadOnlyList<Guid>> IdsAsync(string query = "")
        {
            var response = await Client.GetAsync(new Uri(Route + query, UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            return
            [
                .. body.GetProperty("items")
                    .EnumerateArray()
                    .Select(item => item.GetProperty("id").GetGuid())
            ];
        }

        /// <summary>The product ids <c>?q=</c> returns, in order.</summary>
        public async Task<Guid[]> SearchAsync(string term)
        {
            var response = await Client.GetAsync(
                new Uri($"{Route}?q={Uri.EscapeDataString(term)}", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return [.. (await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid())];
        }

        public async Task<JsonElement> FindAsync(Guid productId)
        {
            var response = await Client.GetAsync(new Uri($"{Route}?limit=200", UriKind.Relative));
            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            return body.GetProperty("items")
                .EnumerateArray()
                .Single(item => item.GetProperty("id").GetGuid() == productId);
        }
    }
}
