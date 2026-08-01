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
/// Tax class CRUD, and the one genuinely awkward rule in it: at most one default per tenant.
/// </summary>
/// <remarks>
/// Every test that touches <c>isDefault</c> creates its own tenant rather than using the
/// shared sandbox. Promotion demotes whatever held the flag, so two such tests sharing a
/// tenant would each change what the other was relying on, and which of them failed would
/// depend on the order xUnit happened to pick.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class TaxClassCrudTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";
    private const string Route = "/api/v1/tax-classes";

    [Fact]
    public async Task A_created_tax_class_belongs_to_the_calling_tenant()
    {
        // Named by the POST row's Exemption in IsolationManifest: a write with no id has no
        // cross-tenant shape to attack, so the tenancy question for it is where the row
        // lands. Answered by reading it back inside the tenant that created it.
        var (client, sandbox) = await SignedInAsync(RoleNames.Owner);

        var created = await client.PostAsJsonAsync(Route, new { name = Unique("Reduced"), rate = 0.1350m });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(sandbox.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The query filter is scoped to this tenant, so finding it here is the assertion.
            Assert.True(await db.TaxClasses.AnyAsync(t => t.Id == id));
        });
    }

    [Fact]
    public async Task A_created_tax_class_comes_back_with_its_location()
    {
        var (client, _) = await SignedInAsync(RoleNames.Manager);

        var name = Unique("Standard");
        var created = await client.PostAsJsonAsync(Route, new { name, rate = 0.2300m });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = body.GetProperty("id").GetGuid();

        Assert.Equal($"{Route}/{id}", created.Headers.Location?.ToString());
        Assert.Equal(name, body.GetProperty("name").GetString());
        Assert.Equal(0.2300m, body.GetProperty("rate").GetDecimal());
        Assert.False(body.GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task The_first_tax_class_is_not_promoted_to_default_automatically()
    {
        // Stated as a test because "the first one becomes the default" is the obvious
        // convenience to add, and it is a rule that fires exactly once in a tenant's
        // lifetime — unreproducible, and unexplainable a year later. Product writes always
        // name a tax class, so nothing reads the default in 2.2.
        var (client, _) = await OwnerOfNewTenantAsync("first-default");

        var created = await client.PostAsJsonAsync(Route, new { name = "Standard", rate = 0.2300m });

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(body.GetProperty("isDefault").GetBoolean());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task The_rates_at_the_edge_of_the_allowed_range_are_accepted(double rate)
    {
        // Inclusive at both ends, mirroring ck_tax_class_rate_range. Zero-rated goods are a
        // real category, and if the API were exclusive it would refuse a row the database
        // would happily hold — the two layers disagreeing about one rule.
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var created = await client.PostAsJsonAsync(
            Route,
            new { name = Unique($"Edge{rate}"), rate = (decimal)rate });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
    }

    [Theory]
    [InlineData("1.0001", "rate")]
    [InlineData("-0.01", "rate")]
    [InlineData("20", "rate")]        // 20% typed as 20 — four hundred times too much
    [InlineData("0.20005", "rate")]   // scale 5 against numeric(6,4): Postgres would round it
    public async Task A_rate_the_column_would_reject_or_round_is_refused(string rate, string field)
    {
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(
            Route,
            new { name = Unique("Bad"), rate = decimal.Parse(rate, Culture) });

        await AssertValidationProblemAsync(response, field);
    }

    [Fact]
    public async Task A_missing_rate_is_refused_rather_than_treated_as_zero()
    {
        // The reason the request record's fields are nullable. A non-nullable decimal binds
        // an omitted rate to 0, which is a *valid* rate — so "you forgot the rate" would
        // silently create a zero-rated class and every product priced against it would
        // undercharge tax until somebody reconciled a return.
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new { name = Unique("No rate") });

        await AssertValidationProblemAsync(response, "rate");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_name_is_refused(string name)
    {
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new { name, rate = 0.2000m });

        await AssertValidationProblemAsync(response, "name");
    }

    [Fact]
    public async Task A_name_past_the_column_length_is_refused()
    {
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(
            Route,
            new { name = new string('x', 61), rate = 0.2000m });

        await AssertValidationProblemAsync(response, "name");
    }

    [Fact]
    public async Task A_bad_name_and_a_bad_rate_are_both_reported()
    {
        // Validation accumulates. Hand-rolled checks default to returning on the first
        // failure, and being told about one field at a time is how a form gets filled in
        // four times.
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var response = await client.PostAsJsonAsync(Route, new { name = "", rate = 9m });

        var errors = await ErrorsOfAsync(response);

        Assert.Equal(["name", "rate"], errors.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task Promoting_a_tax_class_clears_the_previous_default()
    {
        // The clear-then-set path. ux_tax_class_tenant_default is filtered, unique and not
        // deferrable, so doing this in one SaveChanges can violate it depending on the order
        // EF emits the updates.
        var (client, tenantId) = await OwnerOfNewTenantAsync("promote");

        var standard = await CreateAsync(client, "Standard", 0.2300m, isDefault: true);
        var reduced = await CreateAsync(client, "Reduced", 0.1350m, isDefault: true);

        var defaults = await DefaultNamesAsync(tenantId);

        // Exactly one, and it is the second: "make this the default" is what the user meant,
        // so the first is demoted rather than the request refused.
        Assert.Equal(["Reduced"], defaults);
        Assert.NotEqual(standard, reduced);
    }

    [Fact]
    public async Task Promoting_through_put_also_clears_the_previous_default()
    {
        var (client, tenantId) = await OwnerOfNewTenantAsync("promote-put");

        await CreateAsync(client, "Standard", 0.2300m, isDefault: true);
        var zero = await CreateAsync(client, "Zero", 0.0000m, isDefault: false);

        var updated = await client.PutAsJsonAsync(
            $"{Route}/{zero}",
            new { name = "Zero", rate = 0.0000m, isDefault = true });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Equal(["Zero"], await DefaultNamesAsync(tenantId));
    }

    [Fact]
    public async Task Editing_a_tax_class_without_naming_isDefault_leaves_it_alone()
    {
        // Omitted means unchanged, not false. Otherwise correcting a spelling would demote
        // the tenant's default, and nothing on the screen would have said so.
        var (client, tenantId) = await OwnerOfNewTenantAsync("keep-default");

        var standard = await CreateAsync(client, "Standard", 0.2300m, isDefault: true);

        var updated = await client.PutAsJsonAsync(
            $"{Route}/{standard}",
            new { name = "Standard Rate", rate = 0.2300m });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("isDefault").GetBoolean());
        Assert.Equal(["Standard Rate"], await DefaultNamesAsync(tenantId));
    }

    [Fact]
    public async Task Unsetting_the_only_default_is_allowed()
    {
        // Zero defaults is a legal state. Nothing in 2.2 reads the flag, and refusing would
        // mean a tenant could never undo a promotion it made by mistake.
        var (client, tenantId) = await OwnerOfNewTenantAsync("no-default");

        var standard = await CreateAsync(client, "Standard", 0.2300m, isDefault: true);

        var updated = await client.PutAsJsonAsync(
            $"{Route}/{standard}",
            new { name = "Standard", rate = 0.2300m, isDefault = false });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
        Assert.Empty(await DefaultNamesAsync(tenantId));
    }

    [Fact]
    public async Task A_put_replaces_the_name_and_rate()
    {
        var (client, _) = await SignedInAsync(RoleNames.Manager);

        var id = await CreateAsync(client, Unique("Before"), 0.2300m, isDefault: false);

        var newName = Unique("After");
        var updated = await client.PutAsJsonAsync($"{Route}/{id}", new { name = newName, rate = 0.0900m });

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(newName, body.GetProperty("name").GetString());
        Assert.Equal(0.0900m, body.GetProperty("rate").GetDecimal());

        // Returned rather than a 204: the client wants the new updatedAt without a re-read.
        Assert.NotEqual(JsonValueKind.Null, body.GetProperty("updatedAt").ValueKind);
    }

    [Fact]
    public async Task A_cross_tenant_promotion_does_not_demote_the_callers_own_default()
    {
        // The ordering trap, and the test the PUT row in IsolationManifest points at.
        //
        // That row cannot cover this. It sends isDefault: false deliberately, because its
        // job is to prove *tenant A's* row was untouched — and a true there would have the
        // request act on tenant B's data before ever reaching the 404, which is a different
        // failure. This is that different failure.
        //
        // If the clear-then-set ran before the 404 lookup, the caller would demote their own
        // default and then be told the row they aimed at does not exist: a 404 that lies,
        // and a shop whose tax default silently vanished.
        var (client, tenantId) = await OwnerOfNewTenantAsync("cross-promote");

        await CreateAsync(client, "Standard", 0.2300m, isDefault: true);

        var sandbox = await factory.CatalogSandboxAsync();

        var response = await client.PutAsJsonAsync(
            $"{Route}/{sandbox.Catalog.StandardTaxClassId}",
            new { name = "Attempt", rate = 0.1000m, isDefault = true });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // The 404 is the promise; this is the evidence.
        Assert.Equal(["Standard"], await DefaultNamesAsync(tenantId));
    }

    [Fact]
    public async Task A_put_against_an_unknown_id_is_a_404()
    {
        var (client, _) = await SignedInAsync(RoleNames.Owner);

        var response = await client.PutAsJsonAsync(
            $"{Route}/{Guid.CreateVersion7()}",
            new { name = "Nothing", rate = 0.1000m });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Two_concurrent_promotions_leave_one_conflict_and_never_a_500()
    {
        // The race the transaction does not close: both requests arrive with isDefault, both
        // find nothing to clear, and both insert. One loses on the filtered unique index.
        // That is a conflict the caller can act on by re-reading, so it must be a 409 —
        // an unhandled DbUpdateException here would be a 500 and would read as our bug.
        var (client, tenantId) = await OwnerOfNewTenantAsync("default-race");

        using var second = factory.CreateClient();
        second.WithBearer((await second.LoginAsync("default-race", "owner@race.test", Password)).AccessToken);

        var bodies = new[]
        {
            new { name = "Alpha", rate = 0.1000m, isDefault = true },
            new { name = "Beta", rate = 0.2000m, isDefault = true },
        };

        var responses = await Task.WhenAll(
            client.PostAsJsonAsync(Route, bodies[0]),
            second.PostAsJsonAsync(Route, bodies[1]));

        var codes = responses.Select(r => r.StatusCode).Order().ToArray();

        // Either both landed (one promoted, one demoted by clear-then-set) or the loser was
        // told 409. What must never appear is a 500.
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, codes);
        Assert.All(codes, c => Assert.True(
            c is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected status {c}."));

        // Whatever happened, the invariant the index exists to protect still holds.
        Assert.True((await DefaultNamesAsync(tenantId)).Count <= 1);

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task The_list_is_a_cursor_page_rather_than_a_bare_array()
    {
        var (client, _) = await SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(new Uri(Route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // One envelope shape across every list endpoint, so Phase 4.1's generated client
        // gets one Page<T> rather than three response shapes to special-case.
        Assert.Equal(JsonValueKind.Array, body.GetProperty("items").ValueKind);
        Assert.True(body.TryGetProperty("nextCursor", out _));
        Assert.True(body.TryGetProperty("hasMore", out _));
    }

    [Fact]
    public async Task A_malformed_cursor_is_a_400_rather_than_a_500()
    {
        var (client, _) = await SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(new Uri($"{Route}?cursor=!!not-a-cursor!!", UriKind.Relative));

        await AssertValidationProblemAsync(response, "cursor");
    }

    private static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>A name nothing else in the shared sandbox will collide with.</summary>
    private static string Unique(string prefix) => $"{prefix} {Guid.CreateVersion7():N}"[..Math.Min(60, prefix.Length + 33)];

    private static async Task<Guid> CreateAsync(HttpClient client, string name, decimal rate, bool isDefault)
    {
        var response = await client.PostAsJsonAsync(Route, new { name, rate, isDefault });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>The names of every tax class currently flagged default in the tenant.</summary>
    /// <remarks>
    /// A list rather than a single value on purpose: asserting "exactly one" is the point,
    /// and a helper returning the first would hide the very state the index prevents.
    /// </remarks>
    private async Task<List<string>> DefaultNamesAsync(Guid tenantId)
    {
        var names = new List<string>();

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            names = await db.TaxClasses
                .Where(t => t.IsDefault)
                .Select(t => t.Name)
                .ToListAsync();
        });

        return names;
    }

    /// <summary>A client signed into the shared sandbox as one of its three roles.</summary>
    private async Task<(HttpClient Client, CatalogSandbox Sandbox)> SignedInAsync(string role)
    {
        var sandbox = await factory.CatalogSandboxAsync();

        var email = role switch
        {
            RoleNames.Owner => CatalogSandbox.OwnerEmail,
            RoleNames.Manager => CatalogSandbox.ManagerEmail,
            _ => CatalogSandbox.CashierEmail,
        };

        var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(CatalogSandbox.Slug, email, CatalogSandbox.Password)).AccessToken);

        return (client, sandbox);
    }

    /// <summary>
    /// A throwaway tenant with one Owner, for the tests that change which tax class is the
    /// default.
    /// </summary>
    private async Task<(HttpClient Client, Guid TenantId)> OwnerOfNewTenantAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");
        await factory.CreateUserAsync(tenant.Id, "owner@race.test", Password, RoleNames.Owner);

        var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(slug, "owner@race.test", Password)).AccessToken);

        return (client, tenant.Id);
    }

    private static async Task AssertValidationProblemAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        Assert.True((await ErrorsOfAsync(response)).TryGetProperty(field, out _), $"No '{field}' in errors.");
    }

    private static async Task<JsonElement> ErrorsOfAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Compared by field key, never by the whole body: problem+json carries a per-request
        // traceId, so a string comparison would be a different failure every run.
        return body.GetProperty("errors");
    }
}
