using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>Searching, filtering and paging the catalog.</summary>
/// <remarks>
/// Every test here creates its own tenant. The shared sandbox accumulates products from every
/// other catalog test, so a search assertion made against it would be answering "did anything
/// else happen to create a product whose name contains this?" — which is a different question
/// and one whose answer changes with the run order.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ProductListTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";
    private const string Route = "/api/v1/products";

    [Fact]
    public async Task A_search_matches_a_name_regardless_of_case()
    {
        var shop = await NewShopAsync("search-case");

        await shop.CreateAsync("WATER-500", "Still Water 500ml");
        await shop.CreateAsync("BREAD-1", "White Sliced Pan");

        // Contains, not starts-with: this is what ix_product_tenant_name_trgm exists for, and
        // "wat" in the middle of nothing is still the shape a type-ahead sends.
        Assert.Equal(["Still Water 500ml"], await shop.NamesAsync("?q=wat"));
        Assert.Equal(["Still Water 500ml"], await shop.NamesAsync("?q=WAT"));
        Assert.Equal(["Still Water 500ml"], await shop.NamesAsync("?q=Water"));
    }

    [Fact]
    public async Task A_search_matches_in_the_middle_of_a_name()
    {
        var shop = await NewShopAsync("search-contains");

        await shop.CreateAsync("CHEDDAR-1", "Irish Cheddar");

        // The assertion that separates a contains search from a prefix one. A btree can only
        // answer the second.
        Assert.Equal(["Irish Cheddar"], await shop.NamesAsync("?q=hedda"));
    }

    [Fact]
    public async Task A_search_matches_a_sku_exactly_and_case_insensitively()
    {
        var shop = await NewShopAsync("search-sku");

        await shop.CreateAsync("WATER-500", "Still Water 500ml");

        // Exact on the normalised term, against ux_product_tenant_sku. SKUs are short and
        // trigrams need three non-wildcard characters to be selective, so a substring search
        // here would scan while helping nobody — staff type or scan the whole code.
        Assert.Equal(["Still Water 500ml"], await shop.NamesAsync("?q=water-500"));
        Assert.Equal(["Still Water 500ml"], await shop.NamesAsync("?q=WATER-500"));

        // And a partial SKU does not match, which is the trade being made.
        Assert.Empty(await shop.NamesAsync("?q=ATER-5"));
    }

    [Fact]
    public async Task A_percent_in_the_search_term_is_a_literal_percent()
    {
        // Without escaping, q=% is a LIKE pattern matching the entire catalog — the caller
        // would be writing the query rather than searching with it.
        var shop = await NewShopAsync("search-wildcard");

        await shop.CreateAsync("SALE-1", "50% off crackers");
        await shop.CreateAsync("PLAIN-1", "Plain crackers");

        Assert.Equal(["50% off crackers"], await shop.NamesAsync("?q=" + Uri.EscapeDataString("%")));

        // The other two wildcards LIKE understands.
        Assert.Empty(await shop.NamesAsync("?q=" + Uri.EscapeDataString("_")));
        Assert.Empty(await shop.NamesAsync("?q=" + Uri.EscapeDataString("\\")));
    }

    [Fact]
    public async Task A_search_that_matches_nothing_is_an_empty_page_rather_than_an_error()
    {
        var shop = await NewShopAsync("search-empty");

        await shop.CreateAsync("ONE-1", "Something");

        var body = await shop.PageAsync("?q=nothingmatchesthis");

        Assert.Empty(body.GetProperty("items").EnumerateArray());
        Assert.False(body.GetProperty("hasMore").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task An_over_long_search_term_is_refused()
    {
        var shop = await NewShopAsync("search-long");

        var response = await shop.Client.GetAsync(
            new Uri($"{Route}?q={new string('x', 201)}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("q", out _));
    }

    [Fact]
    public async Task Filters_narrow_each_other_rather_than_replacing_each_other()
    {
        var shop = await NewShopAsync("filters");

        var grocery = await shop.CreateCategoryAsync("Grocery");
        var deli = await shop.CreateCategoryAsync("Deli");

        await shop.CreateAsync("A-1", "Apple juice", grocery);
        await shop.CreateAsync("A-2", "Apple tart", deli);
        await shop.CreateAsync("B-1", "Bread", grocery);

        // ANDed: category and term together, not either alone.
        Assert.Equal(["Apple juice"], await shop.NamesAsync($"?q=apple&categoryId={grocery}"));
        Assert.Equal(["Apple juice", "Bread"], await shop.NamesAsync($"?categoryId={grocery}"));
        Assert.Equal(["Apple juice", "Apple tart"], await shop.NamesAsync("?q=apple"));
    }

    [Fact]
    public async Task Another_tenants_category_filters_to_nothing_rather_than_404()
    {
        // It is a filter, not a lookup. The query filter makes it match nothing, and
        // answering 404 would confirm the id exists somewhere.
        var shop = await NewShopAsync("filter-cross");
        var world = await factory.IsolationWorldAsync();

        await shop.CreateAsync("A-1", "Anything");

        var body = await shop.PageAsync($"?categoryId={world.A.Catalog.GroceryCategoryId}");

        Assert.Empty(body.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Deactivated_products_are_hidden_unless_asked_for()
    {
        var shop = await NewShopAsync("active-filter");

        var live = await shop.CreateAsync("LIVE-1", "Live product");
        var dead = await shop.CreateAsync("DEAD-1", "Dead product");

        await shop.Client.PostAsJsonAsync($"{Route}/{dead}/deactivate", new { });

        // Defaults to true, so forgetting the parameter hides it — the register grid is the
        // dominant caller and must never offer an unsellable product.
        Assert.Equal(["Live product"], await shop.NamesAsync(string.Empty));
        Assert.Equal(["Dead product", "Live product"], await shop.NamesAsync("?activeOnly=false"));
        Assert.Equal(["Live product"], await shop.NamesAsync("?activeOnly=true"));

        Assert.NotEqual(live, dead);
    }

    [Fact]
    public async Task Paging_returns_every_product_exactly_once()
    {
        var shop = await NewShopAsync("paging-walk");

        for (var i = 0; i < 7; i++)
        {
            await shop.CreateAsync($"P-{i}", $"Product {i:D2}");
        }

        var seen = new List<Guid>();
        string? cursor = null;

        for (var page = 0; page < 10; page++)
        {
            var query = cursor is null
                ? "?limit=2"
                : $"?limit=2&cursor={Uri.EscapeDataString(cursor)}";

            var body = await shop.PageAsync(query);

            seen.AddRange(body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

            if (!body.GetProperty("hasMore").GetBoolean())
            {
                break;
            }

            cursor = body.GetProperty("nextCursor").GetString();
        }

        Assert.Equal(7, seen.Count);
        Assert.Equal(7, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_product_inserted_behind_the_cursor_does_not_disturb_the_next_page()
    {
        // The exit criterion: stable across concurrent inserts. A page is defined by position
        // rather than by offset, so an insert at an already-served position changes nothing.
        // Offset paging would shift everything after it and serve one row twice.
        var shop = await NewShopAsync("paging-insert");

        await shop.CreateAsync("D-1", "Damson");
        await shop.CreateAsync("E-1", "Elderberry");
        await shop.CreateAsync("F-1", "Fig");

        var first = await shop.PageAsync("?limit=1");

        Assert.True(first.GetProperty("hasMore").GetBoolean());

        var cursor = first.GetProperty("nextCursor").GetString()!;

        // Sorts before the cursor's position, so it belongs to a page already served.
        await shop.CreateAsync("A-1", "Apricot");

        var names = await shop.NamesAsync($"?limit=10&cursor={Uri.EscapeDataString(cursor)}");

        Assert.Equal(["Elderberry", "Fig"], names);
    }

    [Fact]
    public async Task A_cursor_from_another_endpoint_is_refused()
    {
        var shop = await NewShopAsync("cursor-swap");

        var taxClasses = await shop.Client.GetAsync(new Uri("/api/v1/tax-classes?limit=1", UriKind.Relative));
        var body = await taxClasses.Content.ReadFromJsonAsync<JsonElement>();

        // Only meaningful if that list actually paged; otherwise there is no cursor to misuse.
        if (body.GetProperty("nextCursor").ValueKind == JsonValueKind.Null)
        {
            await shop.Client.PostAsJsonAsync("/api/v1/tax-classes", new { name = "Second", rate = 0.1000m });
            taxClasses = await shop.Client.GetAsync(new Uri("/api/v1/tax-classes?limit=1", UriKind.Relative));
            body = await taxClasses.Content.ReadFromJsonAsync<JsonElement>();
        }

        var cursor = body.GetProperty("nextCursor").GetString();

        Assert.NotNull(cursor);

        var response = await shop.Client.GetAsync(
            new Uri($"{Route}?cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative));

        // The sort token in the cursor is what makes this a clean 400 rather than a keyset
        // predicate applied against the wrong column, which would be a 500.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_cursor_minted_in_another_tenant_returns_this_tenants_rows()
    {
        // A cursor is a position, not a capability. It carries no tenant and grants nothing:
        // the query filter and RLS scope the query before the keyset predicate is reached, so
        // replaying one here yields a legal page of this shop's own products. Pinned so that
        // nobody "hardens" it into a signed token later.
        var lender = await NewShopAsync("cursor-lender");
        await lender.CreateAsync("L-1", "Aardvark food");
        await lender.CreateAsync("L-2", "Zebra food");

        var lent = (await lender.PageAsync("?limit=1")).GetProperty("nextCursor").GetString()!;

        var borrower = await NewShopAsync("cursor-borrower");
        await borrower.CreateAsync("B-1", "Marmalade");

        var body = await borrower.PageAsync($"?cursor={Uri.EscapeDataString(lent)}");

        // Marmalade sorts after Aardvark food, so it is on the page — and none of the
        // lender's rows are, because tenancy came from the token and not from the cursor.
        var names = body.GetProperty("items")
            .EnumerateArray()
            .Select(i => i.GetProperty("name").GetString()!)
            .ToArray();

        Assert.Equal(["Marmalade"], names);
    }

    [Fact]
    public async Task A_non_numeric_limit_is_a_400_rather_than_a_500()
    {
        // Known wart: minimal-API binding of int? fails before the handler runs, so this
        // returns a bare 400 with no problem+json body. Pinned so the status is at least
        // right, and so the day someone adds UseStatusCodePages this test says what changed.
        var shop = await NewShopAsync("limit-nan");

        var response = await shop.Client.GetAsync(new Uri($"{Route}?limit=abc", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_limit_past_the_ceiling_is_refused_with_a_field_error()
    {
        var shop = await NewShopAsync("limit-high");

        var response = await shop.Client.GetAsync(new Uri($"{Route}?limit=201", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("limit", out _));
    }

    private async Task<Shop> NewShopAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");
        await factory.CreateUserAsync(tenant.Id, $"owner@{slug}.test", Password, RoleNames.Owner);

        var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(slug, $"owner@{slug}.test", Password)).AccessToken);

        var created = await client.PostAsJsonAsync("/api/v1/tax-classes", new { name = "Standard", rate = 0.2300m });
        var taxClassId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        return new Shop(client, taxClassId);
    }

    /// <summary>One tenant's client and the tax class its products are priced against.</summary>
    private sealed record Shop(HttpClient Client, Guid TaxClassId)
    {
        public async Task<Guid> CreateAsync(string sku, string name, Guid? categoryId = null)
        {
            var response = await Client.PostAsJsonAsync(Route, new
            {
                sku,
                name,
                unitPrice = 1.0000m,
                taxClassId = TaxClassId,
                categoryId,
            });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        public async Task<Guid> CreateCategoryAsync(string name)
        {
            var response = await Client.PostAsJsonAsync("/api/v1/categories", new { name });

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        }

        public async Task<JsonElement> PageAsync(string query)
        {
            var response = await Client.GetAsync(new Uri(Route + query, UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return await response.Content.ReadFromJsonAsync<JsonElement>();
        }

        /// <summary>Names in the order the endpoint returned them, which is (name, id).</summary>
        public async Task<string[]> NamesAsync(string query) =>
            [.. (await PageAsync(query)).GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("name").GetString()!)];
    }
}
