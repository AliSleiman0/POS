using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>Product create, replace, deactivate and activate.</summary>
[Collection(PosApiCollection.Name)]
public sealed class ProductCrudTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";
    private const string Route = "/api/v1/products";

    [Fact]
    public async Task A_created_product_comes_back_normalised_active_and_tracking_stock()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var sku = Sku("lower-case");
        var created = await client.PostAsJsonAsync(Route, new
        {
            sku,
            name = "  Padded Name  ",
            unitPrice = 2.5000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal($"{Route}/{body.GetProperty("id").GetGuid()}", created.Headers.Location?.ToString());

        // NormalizeSku on the write path, not just at the index. Without it "abc" and "ABC"
        // are two products and the unique index cannot tell.
        Assert.Equal(sku.ToUpperInvariant(), body.GetProperty("sku").GetString());
        Assert.Equal("Padded Name", body.GetProperty("name").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.True(body.GetProperty("trackStock").GetBoolean());

        // The enum crosses the wire as its name, not its ordinal, so Phase 4.1's generated
        // client does not inherit a numeric enum that reorders when a member is inserted.
        Assert.Equal("Each", body.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task A_created_product_can_be_read_back_by_id()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, sandbox, Sku("readback"), "Read back");

        var response = await client.GetAsync(new Uri($"{Route}/{id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal("Read back", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_weighed_product_keeps_its_unit()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var created = await client.PostAsJsonAsync(Route, new
        {
            sku = Sku("cheddar"),
            name = "Irish Cheddar",
            unitPrice = 12.5000m,
            unit = "Kilogram",
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Kilogram", body.GetProperty("unit").GetString());
    }

    [Fact]
    public async Task A_product_that_does_not_track_stock_is_stored_that_way()
    {
        // The sentinel case from 2.1, now reachable over HTTP. TrackStock has a store default
        // of true, so an explicit false is exactly the value EF would omit from the INSERT
        // without HasSentinel — and the column default would turn a service item into a
        // stocked one with nothing reporting it.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var created = await client.PostAsJsonAsync(Route, new
        {
            sku = Sku("service"),
            name = "Coffee to Go",
            unitPrice = 2.0000m,
            trackStock = false,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("trackStock").GetBoolean());
    }

    [Fact]
    public async Task A_duplicate_sku_in_the_same_tenant_is_a_conflict()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var sku = Sku("dupe");
        await CreateAsync(client, sandbox, sku, "First");

        var second = await client.PostAsJsonAsync(Route, new
        {
            sku,
            name = "Second",
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        // 409 and a `type` slug, not 400 and an `errors` map. The body is well-formed and
        // would succeed tomorrow if the other product were renamed — nothing about the
        // request itself is wrong, so there is no field to blame.
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("application/problem+json", second.Content.Headers.ContentType?.MediaType);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        // Compared on `type`, never on `detail` or the whole body — problem+json carries a
        // per-request traceId.
        Assert.Equal("https://pos.example/errors/duplicate-sku", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_sku_differing_only_in_case_and_whitespace_is_the_same_sku()
    {
        // Proves NormalizeSku is genuinely on the write path rather than applied for display.
        // The unique index is on the stored value, so it is only as case-insensitive as
        // whatever gets written into it.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var sku = Sku("case");
        await CreateAsync(client, sandbox, sku, "First");

        var second = await client.PostAsJsonAsync(Route, new
        {
            sku = $"  {sku.ToUpperInvariant()}  ",
            name = "Second",
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
    }

    [Fact]
    public async Task The_same_sku_is_accepted_in_two_tenants()
    {
        // Named by the POST row's Exemption in IsolationManifest. ux_product_tenant_sku leads
        // with tenant_id, so two shops using the same supplier code is ordinary and must keep
        // working. Two throwaway tenants, because this is the one thing the shared sandbox
        // cannot express.
        const string sku = "SHARED-SKU-1001";

        var first = await OwnerOfNewTenantAsync("sku-tenant-a");
        var second = await OwnerOfNewTenantAsync("sku-tenant-b");

        Assert.Equal(HttpStatusCode.Created, (await PostAsync(first, sku)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(second, sku)).StatusCode);

        static Task<HttpResponseMessage> PostAsync((HttpClient Client, Guid TaxClassId) tenant, string sku) =>
            tenant.Client.PostAsJsonAsync(Route, new
            {
                sku,
                name = "Still Water 500ml",
                unitPrice = 1.2000m,
                taxClassId = tenant.TaxClassId,
            });
    }

    [Fact]
    public async Task Another_tenants_tax_class_cannot_be_named()
    {
        // Also named by the POST row's Exemption. A 400 on the field rather than a 404: the
        // response is identical for "no such id" and "belongs to another shop", so it does
        // not confirm the id exists anywhere.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);
        var world = await factory.IsolationWorldAsync();

        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = Sku("borrowed"),
            name = "Borrowed tax class",
            unitPrice = 1.0000m,
            taxClassId = world.A.Catalog.StandardTaxClassId,
        });

        await AssertFieldErrorAsync(response, "taxClassId");
    }

    [Fact]
    public async Task Another_tenants_category_cannot_be_named()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);
        var world = await factory.IsolationWorldAsync();

        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = Sku("borrowedcat"),
            name = "Borrowed category",
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
            categoryId = world.A.Catalog.GroceryCategoryId,
        });

        await AssertFieldErrorAsync(response, "categoryId");
    }

    [Fact]
    public async Task A_put_replaces_the_fields_it_carries_and_nulls_the_ones_it_omits()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var created = await client.PostAsJsonAsync(Route, new
        {
            sku = Sku("replace"),
            name = "Before",
            description = "A description",
            categoryId = sandbox.Catalog.GroceryCategoryId,
            unitPrice = 1.0000m,
            unit = "Kilogram",
            trackStock = false,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var newSku = Sku("replaced");
        var updated = await client.PutAsJsonAsync($"{Route}/{id}", new
        {
            sku = newSku,
            name = "After",
            unitPrice = 2.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(newSku.ToUpperInvariant(), body.GetProperty("sku").GetString());
        Assert.Equal("After", body.GetProperty("name").GetString());

        // Replaced, so an omitted description and category are cleared. There is no PATCH.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("description").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("categoryId").ValueKind);

        // The two deliberate exceptions: an absent bool would bind to false and untrack a
        // stocked item, an absent enum to its zero value and turn a weighed product into a
        // countable one. Both silent, so both are left alone instead.
        Assert.Equal("Kilogram", body.GetProperty("unit").GetString());
        Assert.False(body.GetProperty("trackStock").GetBoolean());

        // Returned rather than 204 so the client gets updatedAt without a second call.
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task A_put_onto_a_taken_sku_is_a_conflict_and_leaves_the_row_alone()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var takenSku = Sku("taken");
        await CreateAsync(client, sandbox, takenSku, "Holder");

        var mySku = Sku("mine");
        var id = await CreateAsync(client, sandbox, mySku, "Mine");

        var response = await client.PutAsJsonAsync($"{Route}/{id}", new
        {
            sku = takenSku,
            name = "Renamed",
            unitPrice = 3.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // Nothing was half-applied. The name change travelled in the same request as the SKU
        // change, and the failed save rolled both back.
        var reread = await client.GetAsync(new Uri($"{Route}/{id}", UriKind.Relative));
        var body = await reread.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(mySku.ToUpperInvariant(), body.GetProperty("sku").GetString());
        Assert.Equal("Mine", body.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_put_cannot_resurrect_a_deactivated_product()
    {
        // isActive is not on the request record at all. A PUT edits a deactivated product and
        // leaves it deactivated; bringing it back is an explicit act with its own route, so
        // it cannot happen as a side effect of saving a price change.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, sandbox, Sku("dead"), "Discontinued");

        await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { });

        var updated = await client.PutAsJsonAsync($"{Route}/{id}", new
        {
            sku = Sku("dead2"),
            name = "Still discontinued",
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Deactivating_and_activating_round_trip_and_are_idempotent()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, sandbox, Sku("cycle"), "Seasonal");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { })).StatusCode);

        // Idempotent: a retry after a dropped response must not become an error.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { })).StatusCode);

        Assert.False((await GetAsync(client, id)).GetProperty("isActive").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Route}/{id}/activate", new { })).StatusCode);
        Assert.True((await GetAsync(client, id)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task There_is_no_delete_verb()
    {
        // Stated as a test because it is a decision, not an omission. Sale lines reference
        // products forever, so a delete either orphans history or cascades a customer's sales
        // away — deactivation is the whole mechanism.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, sandbox, Sku("nodelete"), "Permanent");

        var response = await client.DeleteAsync(new Uri($"{Route}/{id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_by_id_route()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);
        var missing = Guid.CreateVersion7();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.GetAsync(new Uri($"{Route}/{missing}", UriKind.Relative))).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new
            {
                sku = Sku("ghost"),
                name = "Ghost",
                unitPrice = 1.0000m,
                taxClassId = sandbox.Catalog.StandardTaxClassId,
            })).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Route}/{missing}/deactivate", new { })).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Route}/{missing}/activate", new { })).StatusCode);
    }

    private static string Sku(string hint) => $"{hint}-{Guid.CreateVersion7():N}"[..Math.Min(64, hint.Length + 33)];

    private static async Task<Guid> CreateAsync(
        HttpClient client,
        CatalogSandbox sandbox,
        string sku,
        string name)
    {
        var response = await client.PostAsJsonAsync(Route, new
        {
            sku,
            name,
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(new Uri($"{Route}/{id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>A tenant of its own, with an Owner and one tax class to price against.</summary>
    private async Task<(HttpClient Client, Guid TaxClassId)> OwnerOfNewTenantAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");
        await factory.CreateUserAsync(tenant.Id, "owner@sku.test", Password, RoleNames.Owner);

        var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(slug, "owner@sku.test", Password)).AccessToken);

        var created = await client.PostAsJsonAsync("/api/v1/tax-classes", new { name = "Standard", rate = 0.2300m });
        var taxClassId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        return (client, taxClassId);
    }

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty(field, out _), $"No '{field}' in errors.");
    }
}
