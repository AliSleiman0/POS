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
/// Category CRUD, and the rule no database constraint can express: a category may not be its
/// own ancestor.
/// </summary>
/// <remarks>
/// <c>fk_category_parent</c> proves the parent exists and shares the tenant, and stops there.
/// Reachability is not something a foreign key can say anything about, so <c>A → B → C → A</c>
/// is four entirely valid rows and the only thing standing between the catalog and a
/// non-terminating breadcrumb renderer is the check in the write path.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CategoryCrudTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/categories";

    [Fact]
    public async Task A_created_category_belongs_to_the_calling_tenant()
    {
        // Named by the POST row's Exemption in IsolationManifest.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, Unique("Bakery"));

        await factory.AsTenantAsync(sandbox.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.True(await db.Categories.AnyAsync(c => c.Id == id));
        });
    }

    [Fact]
    public async Task A_created_category_is_active_and_top_level()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var name = Unique("Frozen");
        var created = await client.PostAsJsonAsync(Route, new { name, sortOrder = 30 });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal($"{Route}/{body.GetProperty("id").GetGuid()}", created.Headers.Location?.ToString());
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal(30, body.GetProperty("sortOrder").GetInt32());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("parentCategoryId").ValueKind);
    }

    [Fact]
    public async Task A_category_can_be_created_beneath_another()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var parent = await CreateAsync(client, Unique("Drinks"));
        var child = await CreateAsync(client, Unique("Juice"), parent);

        var body = await GetOneAsync(client, child);

        Assert.Equal(parent, body.GetProperty("parentCategoryId").GetGuid());
    }

    [Fact]
    public async Task A_category_cannot_be_its_own_parent()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, Unique("Loop"));

        var response = await client.PutAsJsonAsync(
            $"{Route}/{id}",
            new { name = "Loop", parentCategoryId = id });

        await AssertCycleProblemAsync(response);
    }

    [Fact]
    public async Task A_category_cannot_be_reparented_beneath_its_own_descendant()
    {
        // A → B → C, then move A under C. This is the case a one-level "is the proposed
        // parent my direct child?" check waves straight through, which is why the check
        // walks the whole ancestor chain.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var a = await CreateAsync(client, Unique("A"));
        var b = await CreateAsync(client, Unique("B"), a);
        var c = await CreateAsync(client, Unique("C"), b);

        var response = await client.PutAsJsonAsync(
            $"{Route}/{a}",
            new { name = "A", parentCategoryId = c });

        await AssertCycleProblemAsync(response);

        // The row is unchanged, not half-written. The cycle is detected before anything is
        // assigned, so a refused reparent cannot leave the tree in a worse state than it
        // found it.
        var body = await GetOneAsync(client, a);

        Assert.Equal(JsonValueKind.Null, body.GetProperty("parentCategoryId").ValueKind);
    }

    [Fact]
    public async Task A_legal_reparent_is_allowed()
    {
        // The positive control. Without it, a check that refused every reparent would pass
        // both cycle tests above and be entirely broken.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var first = await CreateAsync(client, Unique("First"));
        var second = await CreateAsync(client, Unique("Second"));
        var leaf = await CreateAsync(client, Unique("Leaf"), first);

        var response = await client.PutAsJsonAsync(
            $"{Route}/{leaf}",
            new { name = "Leaf", parentCategoryId = second });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(second, body.GetProperty("parentCategoryId").GetGuid());
    }

    [Fact]
    public async Task Omitting_the_parent_moves_a_category_to_the_top_level()
    {
        // PUT replaces; there is no PATCH. Worth a test because it is the behaviour a client
        // that forgets to send the parent will hit, and "my tree flattened" is a confusing
        // report to receive.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var parent = await CreateAsync(client, Unique("Parent"));
        var child = await CreateAsync(client, Unique("Child"), parent);

        var response = await client.PutAsJsonAsync($"{Route}/{child}", new { name = "Child" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, body.GetProperty("parentCategoryId").ValueKind);
    }

    [Fact]
    public async Task Another_tenants_category_cannot_be_named_as_a_parent()
    {
        // Named by the POST row's Exemption. A 400 on the field rather than a 404: the
        // request is what is wrong, and the answer is identical for "no such id" and "that
        // one belongs to another shop", so it is not an existence oracle.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);
        var world = await factory.IsolationWorldAsync();

        var response = await client.PostAsJsonAsync(
            Route,
            new { name = Unique("Borrowed"), parentCategoryId = world.A.Catalog.GroceryCategoryId });

        await AssertValidationProblemAsync(response, "parentCategoryId");
    }

    [Fact]
    public async Task An_unknown_parent_is_refused()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(
            Route,
            new { name = Unique("Orphan"), parentCategoryId = Guid.CreateVersion7() });

        await AssertValidationProblemAsync(response, "parentCategoryId");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused(string name)
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        await AssertValidationProblemAsync(await client.PostAsJsonAsync(Route, new { name }), "name");
    }

    [Fact]
    public async Task A_name_past_the_column_length_is_refused()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new { name = new string('x', 101) });

        await AssertValidationProblemAsync(response, "name");
    }

    [Fact]
    public async Task A_negative_sort_order_is_refused()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new { name = Unique("Neg"), sortOrder = -1 });

        await AssertValidationProblemAsync(response, "sortOrder");
    }

    [Fact]
    public async Task A_bad_name_and_a_bad_sort_order_are_both_reported()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new { name = "", sortOrder = -5 });

        var errors = await ErrorsOfAsync(response);

        Assert.Equal(["name", "sortOrder"], errors.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Deactivating_hides_a_category_without_touching_its_products()
    {
        // No cascade, deliberately. Hiding a category from a picker is a presentation
        // decision; unselling every product on that shelf is not, and a cascade would do the
        // second while the screen said it was doing the first.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var category = sandbox.Catalog.CheeseCategoryId;

        var deactivated = await client.PostAsJsonAsync($"{Route}/{category}/deactivate", new { });

        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        await factory.AsTenantAsync(sandbox.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.False(await db.Categories.Where(c => c.Id == category).Select(c => c.IsActive).SingleAsync());

            // Coffee still points at it and is still sellable.
            var coffee = await db.Products.SingleAsync(p => p.Id == sandbox.Catalog.CoffeeProductId);

            Assert.Equal(category, coffee.CategoryId);
            Assert.True(coffee.IsActive);
        });

        // Put it back: the sandbox is shared, and leaving Cheese deactivated would change
        // what a later test sees from the list endpoint.
        var reactivated = await client.PostAsJsonAsync($"{Route}/{category}/activate", new { });

        Assert.Equal(HttpStatusCode.NoContent, reactivated.StatusCode);
    }

    [Fact]
    public async Task Deactivating_and_activating_round_trip()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, Unique("Seasonal"));

        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { })).StatusCode);
        Assert.False((await GetOneAsync(client, id, activeOnly: false)).GetProperty("isActive").GetBoolean());

        // The gap the activate routes exist to close. Without them a mis-clicked deactivate
        // is permanent short of running SQL by hand, because PUT does not carry isActive.
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Route}/{id}/activate", new { })).StatusCode);
        Assert.True((await GetOneAsync(client, id)).GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task Deactivating_twice_is_still_a_204()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, Unique("Twice"));

        await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { });
        var again = await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { });

        // Idempotent: a retried request after a dropped response must not become an error.
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
    }

    [Fact]
    public async Task The_list_hides_deactivated_categories_unless_asked()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(client, Unique("Hidden"));
        await client.PostAsJsonAsync($"{Route}/{id}/deactivate", new { });

        // activeOnly defaults to true, so forgetting it hides the row — the safe direction
        // for a picker.
        Assert.DoesNotContain(id, await ListIdsAsync(client, "?limit=200"));
        Assert.Contains(id, await ListIdsAsync(client, "?limit=200&activeOnly=false"));
    }

    [Fact]
    public async Task An_unknown_id_is_a_404_on_every_by_id_route()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);
        var missing = Guid.CreateVersion7();

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PutAsJsonAsync($"{Route}/{missing}", new { name = "Nothing" })).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Route}/{missing}/deactivate", new { })).StatusCode);

        Assert.Equal(
            HttpStatusCode.NotFound,
            (await client.PostAsJsonAsync($"{Route}/{missing}/activate", new { })).StatusCode);
    }

    [Fact]
    public async Task The_list_pages_by_cursor()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var first = await client.GetAsync(new Uri($"{Route}?limit=1", UriKind.Relative));
        var body = await first.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.True(body.GetProperty("hasMore").GetBoolean());

        var cursor = body.GetProperty("nextCursor").GetString();

        Assert.NotNull(cursor);

        var second = await client.GetAsync(
            new Uri($"{Route}?limit=1&cursor={Uri.EscapeDataString(cursor)}", UriKind.Relative));

        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();
        var firstId = body.GetProperty("items")[0].GetProperty("id").GetGuid();
        var secondId = secondBody.GetProperty("items")[0].GetProperty("id").GetGuid();

        Assert.NotEqual(firstId, secondId);
    }

    private static string Unique(string prefix) => $"{prefix}-{Guid.CreateVersion7():N}"[..Math.Min(100, prefix.Length + 33)];

    private static async Task<Guid> CreateAsync(HttpClient client, string name, Guid? parent = null)
    {
        var response = await client.PostAsJsonAsync(Route, new { name, parentCategoryId = parent });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>
    /// Reads one category back off the list, since docs/API.md defines no by-id GET for
    /// categories and this suite does not invent routes to make itself easier to write.
    /// </summary>
    private static async Task<JsonElement> GetOneAsync(HttpClient client, Guid id, bool activeOnly = true)
    {
        var response = await client.GetAsync(
            new Uri($"{Route}?limit=200&activeOnly={activeOnly}", UriKind.Relative));

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == id);
    }

    private static async Task<List<Guid>> ListIdsAsync(HttpClient client, string query)
    {
        var response = await client.GetAsync(new Uri(Route + query, UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return [.. body.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];
    }


    private static async Task AssertCycleProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Compared on `type`, never on `detail` — the slug is the contract clients branch on
        // and the prose is explicitly reworded at will. And never on the whole body, which
        // carries a per-request traceId.
        Assert.Equal(
            "https://pos.example/errors/category-cycle",
            body.GetProperty("type").GetString());
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        Assert.True((await ErrorsOfAsync(response)).TryGetProperty(field, out _), $"No '{field}' in errors.");
    }

    private static async Task<JsonElement> ErrorsOfAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");
}
