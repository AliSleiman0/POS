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
/// <c>costPrice</c> is omitted from the payload for anyone without <c>CanViewMargins</c> —
/// not sent and hidden.
/// </summary>
/// <remarks>
/// <b>The trap this class is arranged around.</b> An "absent for a Cashier" assertion aimed
/// at a product with no recorded cost passes whether the omission works or not, because the
/// field would be absent either way. So every test here uses Still Water, which carries a
/// real cost, and the first test asserts an Owner <i>can</i> see it — establishing that the
/// value exists before anything claims it is missing.
/// <para>
/// <c>CanViewMargins</c> is Owner-only, so a Manager is the interesting caller: they may
/// create and edit products yet must never see the cost. That makes them the one who proves
/// the omission is real rather than a side effect of role checks elsewhere.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ProductMarginTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/products";

    [Fact]
    public async Task An_owner_sees_the_cost_price()
    {
        // First, and load-bearing. Every "costPrice is absent" assertion below is vacuous
        // unless something establishes that there is a cost to be absent.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var body = await GetAsync(client, sandbox.Catalog.WaterProductId);

        Assert.True(body.TryGetProperty("costPrice", out var cost), "An Owner's payload has no costPrice.");
        Assert.Equal(CatalogFixture.WaterCostPrice, cost.GetDecimal());
    }

    [Theory]
    [InlineData(RoleNames.Cashier)]
    [InlineData(RoleNames.Manager)]
    public async Task A_caller_without_margins_gets_no_cost_price_field_at_all(string role)
    {
        var (client, sandbox) = await factory.SignedInAsync(role);

        var body = await GetAsync(client, sandbox.Catalog.WaterProductId);

        // Absent, not null. Invariant 7: a field a role may not see is omitted from the
        // response, because anything that reaches the browser is readable regardless of what
        // the UI does with it.
        Assert.False(
            body.TryGetProperty("costPrice", out _),
            $"A {role}'s payload contains costPrice.");

        // The rest of the product is still there — this is an omitted field, not a refused
        // request.
        Assert.Equal(CatalogFixture.WaterSku, body.GetProperty("sku").GetString());
        Assert.Equal(1.2000m, body.GetProperty("unitPrice").GetDecimal());
    }

    [Theory]
    [InlineData(RoleNames.Cashier)]
    [InlineData(RoleNames.Manager)]
    public async Task The_list_omits_it_too(string role)
    {
        // Two projections, two code paths. An omission applied only to the by-id read would
        // leave the whole catalog's costs on the list endpoint the register calls constantly.
        var (client, sandbox) = await factory.SignedInAsync(role);

        var water = await FindInListAsync(client, sandbox.Catalog.WaterProductId);

        Assert.False(water.TryGetProperty("costPrice", out _), $"A {role}'s list contains costPrice.");
    }

    [Fact]
    public async Task The_owners_list_still_carries_it()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var water = await FindInListAsync(client, sandbox.Catalog.WaterProductId);

        Assert.Equal(CatalogFixture.WaterCostPrice, water.GetProperty("costPrice").GetDecimal());
    }

    [Fact]
    public async Task A_manager_round_tripping_a_product_does_not_wipe_its_cost()
    {
        // The reason writes are ignored rather than refused. A Manager's GET omits costPrice,
        // so the obvious read-modify-write sends it back absent — and PUT is a full
        // replacement. Refusing the write would break every Manager edit; honouring the
        // absence would silently destroy the Owner's cost data on each one.
        var (owner, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var id = await CreateAsync(owner, sandbox, cost: 3.2500m);

        var (manager, _) = await factory.SignedInAsync(RoleNames.Manager);

        var edited = await manager.PutAsJsonAsync($"{Route}/{id}", new
        {
            sku = $"MGR-{id:N}"[..20],
            name = "Renamed by a manager",
            unitPrice = 9.9900m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,

            // costPrice deliberately absent, exactly as the Manager's own GET returned it.
        });

        Assert.Equal(HttpStatusCode.OK, edited.StatusCode);

        var reread = await GetAsync(owner, id);

        Assert.Equal("Renamed by a manager", reread.GetProperty("name").GetString());
        Assert.Equal(3.2500m, reread.GetProperty("costPrice").GetDecimal());
    }

    [Fact]
    public async Task A_manager_cannot_set_a_cost_price_by_sending_one()
    {
        // Ignored, not rejected — the same shape as
        // ForgedTenancyTests.A_tenant_id_in_the_request_body_is_never_honoured. A field a
        // role may not read is a field it may not write.
        var (manager, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var created = await manager.PostAsJsonAsync(Route, new
        {
            sku = $"MGRCOST-{Guid.CreateVersion7():N}"[..24],
            name = "Manager priced",
            unitPrice = 5.0000m,
            costPrice = 9.9900m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // Read back as the Owner, who would see it if it had been stored.
        var (owner, _) = await factory.SignedInAsync(RoleNames.Owner);

        var body = await GetAsync(owner, id);

        Assert.False(body.TryGetProperty("costPrice", out _), "A Manager's costPrice was stored.");
    }

    [Fact]
    public async Task A_manager_who_sends_a_malformed_cost_is_still_told_so()
    {
        // Ignoring the value is not the same as ignoring the field. A number that could never
        // be stored is reported as a validation error whoever sent it, rather than being
        // silently dropped and leaving the caller thinking it took.
        var (manager, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await manager.PostAsJsonAsync(Route, new
        {
            sku = $"MGRBAD-{Guid.CreateVersion7():N}"[..22],
            name = "Manager bad cost",
            unitPrice = 5.0000m,
            costPrice = -1m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("costPrice", out _));
    }

    [Fact]
    public async Task A_cashiers_query_never_mentions_the_cost_column()
    {
        // Stronger than "the field is absent from the JSON", and the reason the endpoint uses
        // two projection expressions rather than one with a ternary. A ternary would compile
        // to CASE WHEN @p THEN cost_price ELSE NULL END, which still reads the column — the
        // value would never leave the server, so the invariant would hold, but there would be
        // nothing to assert. This is what that choice buys.
        var (_, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        await factory.AsTenantAsync(sandbox.TenantId, services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var withoutCost = db.Products
                .AsNoTracking()
                .Select(p => new { p.Id, p.Sku, CostPrice = (decimal?)null })
                .ToQueryString();

            Assert.DoesNotContain("cost_price", withoutCost, StringComparison.Ordinal);

            // The control: the same query naming the column does contain it, so the assertion
            // above is not passing because "cost_price" never appears in any SQL.
            var withCost = db.Products
                .AsNoTracking()
                .Select(p => new { p.Id, p.Sku, p.CostPrice })
                .ToQueryString();

            Assert.Contains("cost_price", withCost, StringComparison.Ordinal);

            return Task.CompletedTask;
        });
    }

    private static async Task<Guid> CreateAsync(HttpClient client, CatalogSandbox sandbox, decimal cost)
    {
        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = $"COST-{Guid.CreateVersion7():N}"[..20],
            name = "Costed product",
            unitPrice = 7.5000m,
            costPrice = cost,
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

    private static async Task<JsonElement> FindInListAsync(HttpClient client, Guid id)
    {
        var response = await client.GetAsync(new Uri($"{Route}?limit=200", UriKind.Relative));
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("items")
            .EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == id);
    }
}
