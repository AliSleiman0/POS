using System.Net;
using System.Net.Http.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// The exact status codes behind every catalog policy.
/// </summary>
/// <remarks>
/// <c>NegativeAuthorizationTests</c> already drives the manifest's <c>Refused</c> lists, but
/// it asserts only that a caller was turned away — 401 and 403 are both acceptable there,
/// because for most rows either would be correct. Here the distinction is the assertion: 401
/// means "we do not know who you are", 403 means "we know, and no". Getting them the wrong
/// way round is how a client shows a login prompt to a signed-in Cashier who simply lacks a
/// permission.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CatalogAuthorizationTests(PosApiFactory factory)
{
    public static TheoryData<string> WriteRoutes => new()
    {
        "/api/v1/products",
        "/api/v1/categories",
        "/api/v1/tax-classes",
    };

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_cashier_is_forbidden_from_creating(string route)
    {
        // 403: authenticated, identified, and refused on CanManageCatalog.
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.PostAsJsonAsync(route, new { name = "Attempt" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_cashier_is_forbidden_from_replacing(string route)
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.PutAsJsonAsync($"{route}/{Guid.CreateVersion7()}", new { name = "Attempt" });

        // 403 rather than the 404 the id would otherwise earn: authorization runs before the
        // handler, so a caller who may not manage the catalog never learns whether the row
        // exists.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/products")]
    [InlineData("/api/v1/categories")]
    public async Task A_cashier_is_forbidden_from_deactivating_and_activating(string route)
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var id = route.Contains("products", StringComparison.Ordinal)
            ? sandbox.Catalog.WaterProductId
            : sandbox.Catalog.GroceryCategoryId;

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"{route}/{id}/deactivate", new { })).StatusCode);

        Assert.Equal(
            HttpStatusCode.Forbidden,
            (await client.PostAsJsonAsync($"{route}/{id}/activate", new { })).StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task An_anonymous_caller_is_unauthorized_on_the_lists(string route)
    {
        // 401, not 403: nothing identified the caller, so there is nobody to refuse.
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_device_token_alone_is_unauthorized_on_the_lists(string route)
    {
        // An enrolled till has a tenant but no user and no role, and CanSell requires one.
        // The device scheme authenticates a machine for the PIN screen; it is not a session.
        var world = await factory.IsolationWorldAsync();

        using var client = factory.CreateClient();
        client.WithDeviceToken(world.B.FrontCounter.DeviceToken);

        var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_cashier_may_read_every_catalog_list(string route)
    {
        // The positive control. Without it, an endpoint that refused everyone would satisfy
        // every negative assertion above — and CanSell exists precisely so a till can price
        // a line without an Owner standing at it.
        var (client, _) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(new Uri(route, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(WriteRoutes))]
    public async Task A_manager_may_write_to_every_catalog_resource(string route)
    {
        // The other positive control: CanManageCatalog admits Manager as well as Owner, so
        // the 403s above are about the policy rather than about everything being Owner-only.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        object body = route switch
        {
            "/api/v1/products" => new
            {
                sku = $"MGR-{Guid.CreateVersion7():N}"[..20],
                name = "Manager created",
                unitPrice = 1.0000m,
                taxClassId = sandbox.Catalog.StandardTaxClassId,
            },
            "/api/v1/tax-classes" => new { name = $"Rate {Guid.CreateVersion7():N}"[..20], rate = 0.1000m },
            _ => new { name = $"Aisle {Guid.CreateVersion7():N}"[..20] },
        };

        var response = await client.PostAsJsonAsync(route, body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_may_read_a_product_by_id()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(
            new Uri($"/api/v1/products/{sandbox.Catalog.WaterProductId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_may_scan_but_may_not_change_the_codes()
    {
        // The asymmetry the barcode routes are built around: scanning is CanSell because a
        // till does it thousands of times a day, while adding and removing codes is
        // CanManageCatalog because it changes what the shop sells.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(new Uri(
                $"/api/v1/products/by-barcode/{CatalogFixture.WaterBarcode}", UriKind.Relative))).StatusCode);

        Assert.Equal(
            HttpStatusCode.OK,
            (await client.GetAsync(new Uri(
                $"/api/v1/products/{sandbox.Catalog.WaterProductId}/barcodes", UriKind.Relative))).StatusCode);

        var added = await client.PostAsJsonAsync(
            $"/api/v1/products/{sandbox.Catalog.WaterProductId}/barcodes",
            new { code = "9990000000002" });

        Assert.Equal(HttpStatusCode.Forbidden, added.StatusCode);

        // 403 rather than the 204 the real id would earn — authorization runs first, so the
        // code is still there afterwards.
        var removed = await client.DeleteAsync(new Uri(
            $"/api/v1/products/{sandbox.Catalog.WaterProductId}/barcodes/{sandbox.Catalog.WaterBarcodeId}",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, removed.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_scan()
    {
        // 401, not 403. Worth its own test because the scan route is the one most likely to be
        // reached for from an unauthenticated context — it is the one a till calls constantly.
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(
            $"/api/v1/products/by-barcode/{CatalogFixture.WaterBarcode}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
