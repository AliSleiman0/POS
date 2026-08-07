using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// <c>GET /sales/by-client-transaction/{id}</c> — what a till asks after it loses an answer.
/// </summary>
/// <remarks>
/// A register that reloads mid-payment holds the GUID it submitted and nothing else. The sale
/// may have been written or the request may never have arrived, and the two need opposite
/// actions from the cashier: one is "read the change out", the other is "take the payment
/// again". This endpoint is the difference between knowing and guessing.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleLookupTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/sales/by-client-transaction";

    [Fact]
    public async Task The_sale_a_key_wrote_can_be_found_by_that_key()
    {
        var (client, tenant) = await factory.TradingTenantAsync();
        var clientTransactionId = Guid.CreateVersion7();

        var sold = await SellAsync(client, tenant, clientTransactionId);

        using var response = await client.GetAsync($"{Route}/{clientTransactionId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var found = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(sold.GetProperty("id").GetGuid(), found.GetProperty("id").GetGuid());

        // The whole sale, not a bare id. The till renders its completion panel straight from
        // this, and the figure it reads out is changeGiven — so a lookup that answered with
        // less would send the cashier back to POST to find out what they owed the customer.
        Assert.Equal(sold.GetProperty("total").GetDecimal(), found.GetProperty("total").GetDecimal());
        Assert.Equal(
            sold.GetProperty("changeGiven").GetDecimal(),
            found.GetProperty("changeGiven").GetDecimal());
        Assert.Equal(sold.GetProperty("saleNumber").GetInt64(), found.GetProperty("saleNumber").GetInt64());
        Assert.Equal(1, found.GetProperty("lines").GetArrayLength());
        Assert.Equal(1, found.GetProperty("tenders").GetArrayLength());
    }

    [Fact]
    public async Task A_key_that_never_reached_the_server_is_a_404()
    {
        // The branch the whole endpoint exists for: the till reloads holding a key, and this
        // 404 is what tells it nothing was charged and the payment can be taken again.
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.GetAsync($"{Route}/{Guid.CreateVersion7()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_key_the_idempotency_store_knows_but_no_sale_used_is_a_404()
    {
        // A key can be recorded against a *refused* attempt — an under-tender, say — which
        // wrote an idempotency record and no sale. Answering from the wrong table would tell a
        // till that a payment it never took had gone through, which is the one lie that costs
        // a customer money in the direction nobody notices.
        var (client, tenant) = await factory.TradingTenantAsync();
        var clientTransactionId = Guid.CreateVersion7();

        using var refused = await client.PostIdempotentAsync(
            "/api/v1/sales",
            new
            {
                clientTransactionId,
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
                tenders = new[] { new { method = "Cash", amount = 0.01m } },
            },
            idempotencyKey: clientTransactionId);

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        using var response = await client.GetAsync($"{Route}/{clientTransactionId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<JsonElement> SellAsync(
        HttpClient client,
        TradingTenant tenant,
        Guid clientTransactionId)
    {
        using var response = await client.PostIdempotentAsync(
            "/api/v1/sales",
            new
            {
                clientTransactionId,
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 2m } },
                tenders = new[] { new { method = "Cash", amount = 5m } },
            },
            // The same GUID in both places, which is what the register sends: docs/API.md, and
            // the reason one lookup can answer for both.
            idempotencyKey: clientTransactionId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
