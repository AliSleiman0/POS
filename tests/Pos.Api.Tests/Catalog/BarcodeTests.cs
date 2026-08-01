using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// Adding, listing and removing a product's codes.
/// </summary>
/// <remarks>
/// Against the shared sandbox, so every code these tests write is made unique — the tenant
/// already holds the fixture's codes and other tests are adding their own. Assertions are
/// therefore "contains", never counts, except where the test created the product itself and
/// owns everything on it.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class BarcodeTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/products";

    [Fact]
    public async Task A_product_can_carry_several_codes()
    {
        // The case one-barcode-per-product modelling gets wrong: a multipack and a re-label
        // are the same product arriving with two codes, and both must scan.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        var first = Code();
        var second = Code();

        await AddAsync(client, productId, first);
        await AddAsync(client, productId, second);

        var codes = await ListCodesAsync(client, productId);

        // Both sides ordered: the list endpoint sorts by code, and the two generated codes go
        // in in whichever order their GUIDs happened to fall.
        Assert.Equal(new[] { first, second }.Order(), codes.Order());
    }

    [Fact]
    public async Task A_created_code_comes_back_with_the_product_it_belongs_to()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);
        var code = Code();

        var created = await client.PostAsJsonAsync($"{Route}/{productId}/barcodes", new { code });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        // The collection, not a single-barcode route: there is no GET for one code.
        Assert.Equal($"{Route}/{productId}/barcodes", created.Headers.Location?.ToString());

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(code, body.GetProperty("code").GetString());
        Assert.Equal(productId, body.GetProperty("productId").GetGuid());
        Assert.False(body.GetProperty("isPrimary").GetBoolean());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_code_is_stored_as_the_label_prints_it()
    {
        // Trimmed, but not upper-cased. The asymmetry with a SKU is deliberate — GS1-128
        // payloads carry case-significant data, so folding a scan changes what it says.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);
        var code = $"aB{Guid.CreateVersion7():N}"[..20];

        var created = await client.PostAsJsonAsync(
            $"{Route}/{productId}/barcodes",
            new { code = $"  {code}  " });

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(code, body.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_primary_flag_is_stored_as_sent_and_nothing_polices_it()
    {
        // Advisory by decision (Phase 2.3): no filtered unique index, so two primaries on one
        // product is possible. Pinned here so the day somebody adds the constraint, this is
        // the test that tells them a decision is being reversed rather than a bug fixed.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AddAsync(client, productId, Code(), isPrimary: true);
        var second = await client.PostAsJsonAsync(
            $"{Route}/{productId}/barcodes",
            new { code = Code(), isPrimary = true });

        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
    }

    [Fact]
    public async Task A_code_already_used_in_this_tenant_is_a_conflict()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var first = await CreateProductAsync(client, sandbox);
        var second = await CreateProductAsync(client, sandbox);

        var code = Code();

        await AddAsync(client, first, code);

        // On a different product, which is the case that matters: a code identifies one item
        // in the shop, so the same label on two products is a data-entry error however
        // reasonable each half looked on its own.
        var clash = await client.PostAsJsonAsync($"{Route}/{second}/barcodes", new { code });

        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Equal("application/problem+json", clash.Content.Headers.ContentType?.MediaType);

        var body = await clash.Content.ReadFromJsonAsync<JsonElement>();

        // The stable slug, not the prose. Clients branch on `type`.
        Assert.Equal(
            "https://pos.example/errors/duplicate-barcode",
            body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_same_code_is_accepted_in_two_tenants()
    {
        // Two shops stocking the same manufacturer's item hold the same code. The index leads
        // with tenant_id precisely so this keeps working, and this is the test that says so.
        var code = Code();

        var (first, firstCatalog) = await OwnerOfNewTenantAsync($"bc-a-{Guid.CreateVersion7():N}"[..20]);
        var (second, secondCatalog) = await OwnerOfNewTenantAsync($"bc-b-{Guid.CreateVersion7():N}"[..20]);

        await AddAsync(first, await CreateProductAsync(first, firstCatalog), code);
        await AddAsync(second, await CreateProductAsync(second, secondCatalog), code);
    }

    [Fact]
    public async Task A_removed_code_is_gone_and_stays_gone()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);
        var code = Code();
        var barcodeId = await AddAsync(client, productId, code);

        var removed = await client.DeleteAsync(
            new Uri($"{Route}/{productId}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty(await ListCodesAsync(client, productId));

        // 404 rather than a second 204. Deleting a code that is not there means the client is
        // working from a stale list, and that is worth surfacing.
        var again = await client.DeleteAsync(
            new Uri($"{Route}/{productId}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
    }

    [Fact]
    public async Task A_removed_code_frees_the_value_for_another_product()
    {
        // Barcodes may be deleted where products may not, and this is what that buys: a label
        // typed onto the wrong product is correctable rather than permanently burnt.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var wrong = await CreateProductAsync(client, sandbox);
        var right = await CreateProductAsync(client, sandbox);

        var code = Code();
        var barcodeId = await AddAsync(client, wrong, code);

        await client.DeleteAsync(new Uri($"{Route}/{wrong}/barcodes/{barcodeId}", UriKind.Relative));

        await AddAsync(client, right, code);
    }

    [Fact]
    public async Task A_code_addressed_under_the_wrong_product_is_not_found()
    {
        // The handler matches on both ids. Matching on the barcode alone would let a caller
        // delete any code in the tenant by naming any product they could see.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var owner = await CreateProductAsync(client, sandbox);
        var bystander = await CreateProductAsync(client, sandbox);

        var code = Code();
        var barcodeId = await AddAsync(client, owner, code);

        var response = await client.DeleteAsync(
            new Uri($"{Route}/{bystander}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        // And the code is still where it was, which is what the 404 is claiming.
        Assert.Contains(code, await ListCodesAsync(client, owner));
    }

    [Fact]
    public async Task A_product_with_no_codes_lists_an_empty_array_rather_than_a_404()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        Assert.Empty(await ListCodesAsync(client, productId));
    }

    [Fact]
    public async Task An_unknown_product_has_no_barcode_collection()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await client.GetAsync(
            new Uri($"{Route}/{Guid.CreateVersion7()}/barcodes", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_code_cannot_be_added_to_a_product_that_does_not_exist()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await client.PostAsJsonAsync(
            $"{Route}/{Guid.CreateVersion7()}/barcodes",
            new { code = Code() });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("toolong")]
    public async Task A_code_the_column_would_reject_is_refused_per_field(string value)
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        object body = value switch
        {
            "missing" => new { },
            "toolong" => new { code = new string('9', Barcode.CodeMaxLength + 1) },
            _ => new { code = value },
        };

        var response = await client.PostAsJsonAsync($"{Route}/{productId}/barcodes", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("code", out _), "No 'code' in errors.");
    }

    [Fact]
    public async Task A_code_at_the_top_of_the_column_is_accepted()
    {
        // The positive control for the theory above: a handler that refused every code would
        // pass all four of those rows.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AddAsync(client, productId, new string('7', Barcode.CodeMaxLength));
    }

    private static string Code() => $"BC{Guid.CreateVersion7():N}"[..24];

    private async Task<(HttpClient Client, SeededCatalog Catalog)> OwnerOfNewTenantAsync(string slug)
    {
        // A throwaway tenant, because "the same code in two tenants" cannot be asked of one.
        const string email = "owner@barcode.test";

        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");

        await factory.CreateUserAsync(tenant.Id, email, CatalogSandbox.Password, RoleNames.Owner);

        var catalog = await CatalogFixture.WriteAsync(factory, tenant.Id);

        var client = factory.CreateClient();

        client.WithBearer(
            (await client.LoginAsync(slug, email, CatalogSandbox.Password)).AccessToken);

        return (client, catalog);
    }

    private static async Task<Guid> AddAsync(
        HttpClient client,
        Guid productId,
        string code,
        bool isPrimary = false)
    {
        var response = await client.PostAsJsonAsync(
            $"{Route}/{productId}/barcodes",
            new { code, isPrimary });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, CatalogSandbox sandbox) =>
        await CreateProductAsync(client, sandbox.Catalog);

    private static async Task<Guid> CreateProductAsync(HttpClient client, SeededCatalog catalog)
    {
        var response = await client.PostAsJsonAsync(Route, new
        {
            sku = $"BCP-{Guid.CreateVersion7():N}"[..20],
            name = "Barcoded product",
            unitPrice = 1.5000m,
            taxClassId = catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<IReadOnlyList<string>> ListCodesAsync(HttpClient client, Guid productId)
    {
        var response = await client.GetAsync(
            new Uri($"{Route}/{productId}/barcodes", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        return [.. body.EnumerateArray().Select(item => item.GetProperty("code").GetString()!)];
    }
}
