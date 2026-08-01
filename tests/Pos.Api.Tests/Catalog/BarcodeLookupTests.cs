using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Endpoints;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// <c>GET /products/by-barcode/{code}</c> — the hottest read in the application.
/// </summary>
/// <remarks>
/// Every scan of every sale goes through it, so the two properties it has to hold are that
/// the answer is complete enough to price a line without a second call, and that getting it
/// costs one round trip.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class BarcodeLookupTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/products/by-barcode";

    [Fact]
    public async Task A_scan_returns_the_product_its_price_and_its_tax_rate()
    {
        // The whole point of the endpoint: a register that had to fetch the tax class
        // separately would pay a second round trip per item scanned.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var body = await ScanAsync(client, CatalogFixture.WaterBarcode);

        Assert.Equal(sandbox.Catalog.WaterProductId, body.GetProperty("productId").GetGuid());
        Assert.Equal(CatalogFixture.WaterSku, body.GetProperty("sku").GetString());
        Assert.Equal(CatalogFixture.WaterName, body.GetProperty("name").GetString());
        Assert.Equal(1.2000m, body.GetProperty("unitPrice").GetDecimal());

        Assert.Equal(sandbox.Catalog.StandardTaxClassId, body.GetProperty("taxClassId").GetGuid());
        Assert.Equal(0.2300m, body.GetProperty("taxRate").GetDecimal());

        // And which code was scanned, so a till showing "scanned as …" does not have to
        // remember what it sent.
        Assert.Equal(CatalogFixture.WaterBarcode, body.GetProperty("code").GetString());
        Assert.Equal(sandbox.Catalog.WaterBarcodeId, body.GetProperty("barcodeId").GetGuid());
    }

    [Fact]
    public async Task Either_of_a_products_codes_scans_to_the_same_product()
    {
        // A multipack and the single carry different codes and are the same product. This is
        // the assertion that fails if the model is ever "simplified" to one code per product.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var single = await ScanAsync(client, CatalogFixture.WaterBarcode);
        var multipack = await ScanAsync(client, CatalogFixture.WaterMultipackBarcode);

        Assert.Equal(sandbox.Catalog.WaterProductId, single.GetProperty("productId").GetGuid());
        Assert.Equal(sandbox.Catalog.WaterProductId, multipack.GetProperty("productId").GetGuid());

        // Different barcode rows, though — which is how the two are told apart afterwards.
        Assert.NotEqual(
            single.GetProperty("barcodeId").GetGuid(),
            multipack.GetProperty("barcodeId").GetGuid());
    }

    [Fact]
    public async Task An_unknown_code_is_not_found()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(
            new Uri($"{Route}/0000000000000", UriKind.Relative));

        // 404, which the register turns into "unknown item — add it?" rather than an error
        // dialog. It is the ordinary case at a till, not a failure.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_whitespace_scan_is_not_found_rather_than_a_validation_problem()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(new Uri($"{Route}/%20%20", UriKind.Relative));

        // Every answer this endpoint gives has to be one the register already knows how to
        // render, and there are two of those: an item, or nothing.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_scan_is_matched_as_the_label_prints_it()
    {
        // Trimmed — scanners append terminators and pastes bring whitespace — but not
        // case-folded. Both halves matter and this asserts the first.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var body = await ScanAsync(client, $"%20{CatalogFixture.WaterBarcode}%20");

        Assert.Equal(sandbox.Catalog.WaterProductId, body.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task A_deactivated_product_still_scans_and_says_it_is_not_for_sale()
    {
        // Deliberately not filtered out. "Unknown item, add it?" against a product that was
        // withdrawn last week is how a cashier recreates it as a duplicate; the register needs
        // to be able to say "this is not for sale" instead.
        var (manager, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(manager, sandbox);
        var code = Code();

        await AddCodeAsync(manager, productId, code);

        var deactivated = await manager.PostAsJsonAsync($"/api/v1/products/{productId}/deactivate", new { });

        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        var (cashier, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var body = await ScanAsync(cashier, code);

        Assert.Equal(productId, body.GetProperty("productId").GetGuid());
        Assert.False(body.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task A_code_that_exists_in_both_tenants_resolves_to_the_callers_own_product()
    {
        // Named in the isolation manifest as this endpoint's exemption, and the reason it is
        // exempt: the two tenants hold identical catalogs, so the by-id theory's 404 is the
        // wrong assertion here. A caller in tenant B scanning a code both tenants hold must
        // get a 200 carrying B's product — not A's, and not nothing.
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(Actor.CashierOfB, world);

        var body = await ScanAsync(client, CatalogFixture.WaterBarcode);

        Assert.Equal(world.B.Catalog.WaterProductId, body.GetProperty("productId").GetGuid());
        Assert.Equal(world.B.Catalog.WaterBarcodeId, body.GetProperty("barcodeId").GetGuid());

        // Stated as its own assertion because it is the failure this is really about, and the
        // equality above would also hold if the ids happened to collide.
        Assert.NotEqual(world.A.Catalog.WaterProductId, body.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task An_owner_sees_the_cost_price_on_a_scan()
    {
        // First, and load-bearing: the omission assertions below are vacuous unless something
        // establishes there is a cost to omit. Same arrangement as ProductMarginTests.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var body = await ScanAsync(client, CatalogFixture.WaterBarcode);

        Assert.True(body.TryGetProperty("costPrice", out var cost), "An Owner's scan has no costPrice.");
        Assert.Equal(CatalogFixture.WaterCostPrice, cost.GetDecimal());
    }

    [Theory]
    [InlineData(RoleNames.Cashier)]
    [InlineData(RoleNames.Manager)]
    public async Task A_caller_without_margins_scans_without_a_cost_price(string role)
    {
        // The hot path is the one a Cashier calls constantly, so an omission that covered the
        // product endpoints and missed this one would leak the whole catalog's costs a scan
        // at a time.
        var (client, _) = await factory.SignedInAsync(role);

        var body = await ScanAsync(client, CatalogFixture.WaterBarcode);

        Assert.False(body.TryGetProperty("costPrice", out _), $"A {role}'s scan contains costPrice.");

        // Absent, not refused: the rest of the payload is intact.
        Assert.Equal(1.2000m, body.GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task A_scan_reaches_all_three_tables_in_one_statement()
    {
        // The real query the endpoint runs, not a rebuilt equivalent — that is why
        // ProductEndpoints.LookupQuery is internal. An accidental N+1 here is a slow till and
        // it would be invisible to a test that only checked which fields came back.
        await WithLookupSqlAsync(canViewMargins: false, sql =>
        {
            // One driving table, and the other two reached by joining rather than by a second
            // query. EF emits each tenant query filter as an uncorrelated derived table, so
            // the SQL contains three SELECTs and still makes one round trip — counting them
            // would assert the shape of the filter rather than the cost of the read.
            Assert.Equal(1, Occurrences(sql, "FROM barcode"));
            Assert.Equal(2, Occurrences(sql, "INNER JOIN"));

            Assert.Contains("product", sql, StringComparison.Ordinal);
            Assert.Contains("tax_class", sql, StringComparison.Ordinal);

            // Every one of the three carries the tenant predicate. A join is the one place a
            // tenant-scoped read can quietly widen: the driving table is filtered, the joined
            // ones are reached through it, and a missing filter there is invisible until two
            // tenants happen to share an id.
            Assert.Equal(3, Occurrences(sql, "tenant_id = @ef_filter__CurrentTenantId"));
        });
    }

    [Fact]
    public async Task A_cashiers_scan_never_names_the_cost_column()
    {
        // Two projections rather than one with a ternary, for the reason spelled out in
        // ProductEndpoints: a ternary compiles to CASE WHEN, which still reads the column.
        await WithLookupSqlAsync(
            canViewMargins: false,
            sql => Assert.DoesNotContain("cost_price", sql, StringComparison.Ordinal));

        // The control. Without it the assertion above would pass on a query that had stopped
        // selecting anything at all.
        await WithLookupSqlAsync(
            canViewMargins: true,
            sql => Assert.Contains("cost_price", sql, StringComparison.Ordinal));
    }

    private async Task WithLookupSqlAsync(bool canViewMargins, Action<string> assert)
    {
        var (_, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        await factory.AsTenantAsync(sandbox.TenantId, services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            assert(ProductEndpoints
                .LookupQuery(db, CatalogFixture.WaterBarcode, canViewMargins)
                .ToQueryString());

            return Task.CompletedTask;
        });
    }

    private static int Occurrences(string sql, string term)
    {
        var count = 0;

        for (var at = sql.IndexOf(term, StringComparison.Ordinal); at >= 0;
             at = sql.IndexOf(term, at + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static string Code() => $"SCAN{Guid.CreateVersion7():N}"[..24];

    private static async Task<JsonElement> ScanAsync(HttpClient client, string code)
    {
        var response = await client.GetAsync(new Uri($"{Route}/{code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task AddCodeAsync(HttpClient client, Guid productId, string code)
    {
        var response = await client.PostAsJsonAsync(
            $"/api/v1/products/{productId}/barcodes",
            new { code });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, CatalogSandbox sandbox)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku = $"SCAN-{Guid.CreateVersion7():N}"[..20],
            name = "Scannable product",
            unitPrice = 2.5000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
