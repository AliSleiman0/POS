using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// <c>POST /sales/quote</c> — the authoritative total, without committing anything.
/// </summary>
/// <remarks>
/// It exists so the register never needs a second implementation of tax and discount rules.
/// Two implementations will disagree eventually, and the place that surfaces is a customer
/// disputing a receipt at a counter.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleQuoteTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/sales/quote";

    [Fact]
    public async Task A_quote_prices_a_cart_without_writing_anything()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 2m } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2.95m, body.GetProperty("total").GetDecimal());

        // No identity, because nothing was committed. A quote that carried a sale id would
        // invite a client to treat it as one.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("id").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("saleNumber").ValueKind);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Empty(await db.StockMovements.ToListAsync());
        });
    }

    [Fact]
    public async Task A_quote_needs_no_register_shift_or_tenders()
    {
        // Deliberately: a register showing a running total has not chosen a shift or taken
        // money yet, and requiring either would make the endpoint useless for the one job it
        // has. Only POST /sales validates them.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_quote_requires_no_idempotency_key()
    {
        // It writes nothing, so a replay is simply the same arithmetic again. Requiring a key
        // would be ceremony that buys nothing and that a register would have to invent one
        // for on every keystroke.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_quote_applies_the_tenants_cash_rounding()
    {
        // 5-cent rounding, so the register can show the amount the customer will actually
        // hand over rather than one the till would then change.
        var (client, tenant) = await factory.TradingTenantAsync(cashRoundingIncrement: 0.05m);

        using var response = await client.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 2m } },
        });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 2.95 is already on a 5-cent boundary; the coffee below is not.
        Assert.Equal(2.95m, body.GetProperty("total").GetDecimal());
        Assert.Equal(0m, body.GetProperty("roundingAdjustment").GetDecimal());

        using var coffee = await client.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = tenant.Catalog.CoffeeProductId, quantity = 1m } },
        });

        var coffeeBody = await coffee.Content.ReadFromJsonAsync<JsonElement>();

        // 4.50 + 23% = 5.535 -> 5.54 payable -> 5.55 in coins, recorded as +0.01.
        Assert.Equal(5.55m, coffeeBody.GetProperty("total").GetDecimal());
        Assert.Equal(0.01m, coffeeBody.GetProperty("roundingAdjustment").GetDecimal());
    }

    [Fact]
    public async Task A_cross_tenant_product_is_refused()
    {
        // The row the isolation exemption for this endpoint names. The same 400 an unknown id
        // gets, because both routes build their cart through one shared function.
        var (client, _) = await factory.TradingTenantAsync();
        var world = await factory.IsolationWorldAsync();

        using var response = await client.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = world.A.Catalog.WaterProductId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("lines[0].productId", out _));
    }

    [Fact]
    public async Task A_cashier_may_quote()
    {
        // CanSell, and the positive control for the negative-authorization row: a quote is
        // what the register asks for on every scan.
        var (_, tenant) = await factory.TradingTenantAsync();

        await factory.CreateUserAsync(
            tenant.TenantId, "quoter@trading.test", TradingTenant.Password, RoleNames.Cashier, "Robin Vale");

        using var cashier = factory.CreateClient();
        cashier.WithBearer((await cashier.LoginAsync(
            tenant.Slug, "quoter@trading.test", TradingTenant.Password)).AccessToken);

        using var response = await cashier.PostAsJsonAsync(Route, new
        {
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
