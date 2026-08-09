using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Tests.Audit;

/// <summary>
/// The reprint mark, decided by the server.
/// </summary>
/// <remarks>
/// This closes the debt Phase 6.2 recorded and DECISIONS.md left owing. Until now the mark was
/// applied by whoever was rendering, so a client that chose not to send it printed an unmarked
/// duplicate and nothing could contradict it — which §6.2 correctly called a refund-fraud
/// vector. It is now derived from append-only entries the caller cannot suppress.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ReceiptIssueTests(PosApiFactory factory)
{
    [Fact]
    public async Task The_first_copy_of_a_receipt_is_not_a_reprint()
    {
        var (client, tenant) = await factory.TradingTenantAsync();
        var saleId = await SellAsync(client, tenant);

        var receipt = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{saleId}/receipt");

        Assert.False(receipt.GetProperty("isReprint").GetBoolean());
        Assert.Equal(1, receipt.GetProperty("issueNumber").GetInt32());
    }

    [Fact]
    public async Task The_second_copy_of_a_receipt_is_marked_as_a_reprint()
    {
        var (client, tenant) = await factory.TradingTenantAsync();
        var saleId = await SellAsync(client, tenant);

        await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{saleId}/receipt");
        var second = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{saleId}/receipt");

        // The client sent nothing to say so, and could not have suppressed it. That is the
        // whole difference from the Phase 6 behaviour.
        Assert.True(second.GetProperty("isReprint").GetBoolean());
        Assert.Equal(2, second.GetProperty("issueNumber").GetInt32());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entries = await db.AuditEntries
                .Where(a => a.Action == AuditAction.ReceiptIssued && a.EntityId == saleId)
                .ToListAsync();

            Assert.Equal(2, entries.Count);
        });
    }

    [Fact]
    public async Task Each_sale_counts_its_own_issues()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var first = await SellAsync(client, tenant);
        var second = await SellAsync(client, tenant);

        await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{first}/receipt");
        await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{first}/receipt");

        var other = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{second}/receipt");

        // A count that was per-tenant rather than per-sale would mark every receipt in a busy
        // shop as a reprint by mid-morning, and the mark would stop meaning anything.
        Assert.False(other.GetProperty("isReprint").GetBoolean());
        Assert.Equal(1, other.GetProperty("issueNumber").GetInt32());
    }

    [Fact]
    public async Task A_receipt_that_does_not_exist_records_nothing()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.GetAsync(
            new Uri($"/api/v1/sales/{Guid.NewGuid()}/receipt", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The 404 comes before the write. An entry for a disclosure that did not happen
            // would let anybody with a valid session pad the log with noise.
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditAction.ReceiptIssued));
        });
    }

    private static async Task<Guid> SellAsync(HttpClient client, TradingTenant tenant)
    {
        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
