using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// <c>GET /stock/{productId}/movements</c> — the ledger, which answers "why is on hand
/// wrong?" rather than "what is on hand".
/// </summary>
/// <remarks>
/// The page-walk is the test that matters. This is the only list in the application keyed on
/// a <see cref="DateTimeOffset"/> rather than a name, so it is the only one where the cursor
/// round-trips a timestamp — and a cursor whose precision did not survive base64 and JSON
/// would skip or repeat rows silently, in a ledger that is supposed to add up.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class StockMovementListTests(PosApiFactory factory)
{
    [Fact]
    public async Task The_ledger_reads_oldest_first()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AdjustAsync(client, productId, "Receive", 10m, "First");
        await AdjustAsync(client, productId, "Waste", -1m, "Second");
        await AdjustAsync(client, productId, "Adjust", 2m, "Third");

        var reasons = await ReasonsAsync(client, productId);

        // The order a person reads a history in, and the order a rebuild replays it in.
        Assert.Equal(["First", "Second", "Third"], reasons);
    }

    [Fact]
    public async Task Walking_the_ledger_a_page_at_a_time_sees_every_row_exactly_once()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        for (var i = 0; i < 7; i++)
        {
            await AdjustAsync(client, productId, "Receive", 1m, $"Delivery {i}");
        }

        var seen = new List<string>();
        string? cursor = null;

        do
        {
            var url = $"/api/v1/stock/{productId}/movements?limit=2"
                + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

            var response = await client.GetAsync(new Uri(url, UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            seen.AddRange(body.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("reason").GetString()!));

            cursor = body.GetProperty("hasMore").GetBoolean()
                ? body.GetProperty("nextCursor").GetString()
                : null;
        }
        while (cursor is not null);

        // Exact sequence, not a count: a cursor that lost sub-second precision would repeat a
        // row or skip one, and both keep the count plausible.
        Assert.Equal(
            [.. Enumerable.Range(0, 7).Select(i => $"Delivery {i}")],
            seen);
    }

    [Fact]
    public async Task A_movement_carries_everything_needed_to_explain_itself()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AdjustAsync(client, productId, "Waste", -3m, "Dropped a crate");

        var response = await client.GetAsync(
            new Uri($"/api/v1/stock/{productId}/movements", UriKind.Relative));

        var movement = (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items")
            .EnumerateArray()
            .Single();

        Assert.Equal("Waste", movement.GetProperty("type").GetString());
        Assert.Equal(-3.0000m, movement.GetProperty("quantity").GetDecimal());
        Assert.Equal("Dropped a crate", movement.GetProperty("reason").GetString());

        // Who and when, which is the whole reason a shrinkage question can be answered at all.
        Assert.Equal(sandbox.ManagerId, movement.GetProperty("performedBy").GetGuid());
        Assert.True(movement.GetProperty("occurredAt").GetDateTimeOffset() > DateTimeOffset.MinValue);

        // No sale behind a manual adjustment. Phase 3 fills this in.
        Assert.Equal(JsonValueKind.Null, movement.GetProperty("saleId").ValueKind);
    }

    [Fact]
    public async Task A_product_with_no_movements_has_an_empty_ledger_rather_than_a_404()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        Assert.Empty(await ReasonsAsync(client, productId));
    }

    [Fact]
    public async Task An_unknown_product_has_no_ledger()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await client.GetAsync(
            new Uri($"/api/v1/stock/{Guid.CreateVersion7()}/movements", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_may_not_read_the_ledger()
    {
        // CanManageCatalog, unlike the on-hand list. The ledger names who moved what, which
        // is staff information rather than shop-floor information.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var response = await client.GetAsync(
            new Uri($"/api/v1/stock/{sandbox.Catalog.WaterProductId}/movements", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_cursor_from_another_list_is_refused()
    {
        // The sort token travels in the cursor, so a /products cursor applied here is
        // rejected rather than comparing a name against a timestamp and failing at the
        // database as a 500.
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var products = await client.GetAsync(new Uri("/api/v1/products?limit=1", UriKind.Relative));
        var body = await products.Content.ReadFromJsonAsync<JsonElement>();

        var foreign = body.GetProperty("nextCursor").GetString();

        Assert.NotNull(foreign);

        var (_, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await client.GetAsync(new Uri(
            $"/api/v1/stock/{sandbox.Catalog.WaterProductId}/movements?cursor={Uri.EscapeDataString(foreign)}",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static async Task<IReadOnlyList<string>> ReasonsAsync(HttpClient client, Guid productId)
    {
        var response = await client.GetAsync(
            new Uri($"/api/v1/stock/{productId}/movements", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return
        [
            .. body.GetProperty("items")
                .EnumerateArray()
                .Select(item => item.GetProperty("reason").GetString()!)
        ];
    }

    private static async Task AdjustAsync(
        HttpClient client,
        Guid productId,
        string type,
        decimal quantity,
        string reason)
    {
        var response = await client.PostAsJsonAsync(
            "/api/v1/stock/adjustments",
            new { productId, type, quantity, reason });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private static async Task<Guid> CreateProductAsync(HttpClient client, CatalogSandbox sandbox)
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku = $"LDG-{Guid.CreateVersion7():N}"[..20],
            name = "Ledgered product",
            unitPrice = 1.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
