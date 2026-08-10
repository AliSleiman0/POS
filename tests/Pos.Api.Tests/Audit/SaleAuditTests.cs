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
/// What a sale records about the decisions a person made while ringing it.
/// </summary>
/// <remarks>
/// Every entry here is written inside the sale writer's own transaction, which is what makes
/// "an audit entry cannot exist for work that rolled back" true rather than nearly true. The
/// rollback case has its own test at the bottom.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleAuditTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_price_override_records_the_catalog_price_it_replaced()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, line: new
        {
            productId = tenant.Catalog.WaterProductId,
            quantity = 1m,
            unitPriceOverride = 0.50m,
        });

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.PriceOverridden);

            // The whole point of the entry. `SaleLine` keeps the price that was charged and
            // a flag saying it was overridden — it has never held the price that was
            // replaced, and without that number "overridden" says nothing about how much the
            // shop gave away.
            Assert.Equal(nameof(SaleLine), entry.EntityType);
            Assert.Contains("1.2", entry.Before!, StringComparison.Ordinal);
            Assert.Contains("0.50", entry.After!, StringComparison.Ordinal);
            Assert.Contains(saleId.ToString(), entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_line_discount_and_a_cart_discount_are_recorded_separately()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 1m, discountAmount = 0.10m },
            },
            cartDiscountAmount = 0.25m,
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entries = await db.AuditEntries
                .Where(a => a.Action == AuditAction.DiscountApplied)
                .ToListAsync();

            // Two, and distinguishable. "Line or cart" in the phase table means both are
            // recorded, and an owner asking "who is discounting whole baskets?" needs to tell
            // them apart — a single merged entry would answer neither question.
            Assert.Equal(2, entries.Count);

            var line = entries.Single(e => e.EntityType == nameof(SaleLine));
            var cart = entries.Single(e => e.EntityType == nameof(Sale));

            Assert.Contains("0.10", line.After!, StringComparison.Ordinal);
            Assert.Contains("0.25", cart.After!, StringComparison.Ordinal);
            Assert.Contains("cart", cart.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task An_ordinary_sale_records_nothing()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, line: new
        {
            productId = tenant.Catalog.WaterProductId,
            quantity = 2m,
        });

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The rule that keeps the log readable. A shop rings hundreds of sales a day; if
            // an ordinary one filed an entry, nobody would ever read the log and the four
            // rows that matter would be buried under thousands that do not.
            Assert.Empty(await db.AuditEntries.ToListAsync());
        });
    }

    [Fact]
    public async Task Voiding_a_sale_records_the_reason()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, line: new
        {
            productId = tenant.Catalog.WaterProductId,
            quantity = 1m,
        });

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.SaleVoided);

            Assert.Equal(saleId, entry.EntityId);
            Assert.Contains("Completed", entry.Before!, StringComparison.Ordinal);
            Assert.Contains("Rung up twice", entry.After!, StringComparison.Ordinal);
            Assert.NotNull(entry.ActorId);
        });
    }

    [Fact]
    public async Task A_refund_records_both_sales()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, line: new
        {
            productId = tenant.Catalog.WaterProductId,
            quantity = 2m,
        });

        var lineId = await FirstLineIdAsync(client, saleId);

        using var refunded = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/refund", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            reason = "Faulty",
            lines = new[] { new { saleLineId = lineId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.Created, refunded.StatusCode);

        var refundId = (await refunded.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.RefundIssued);

            // Both ends, because the question is always "how much has come back against this
            // sale?" and the refund row alone cannot answer it from the original's side.
            Assert.Equal(refundId, entry.EntityId);
            Assert.Contains(saleId.ToString(), entry.After!, StringComparison.Ordinal);
            Assert.Contains("Faulty", entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_sale_that_fails_after_pricing_leaves_no_entry()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        // Under-tendered, which is refused by the writer's own transaction — after the cart
        // has been priced and the discount has been authorised, so the audit call site has
        // been reached and the work still has to leave nothing behind.
        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 1m, discountAmount = 0.10m },
            },
            tenders = new[] { new { method = "Cash", amount = 0.01m } },
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // An entry surviving its own failure is worse than a missing one: it reports a
            // discount somebody gave on a sale that never happened, and there is nothing left
            // to contradict it.
            Assert.Empty(await db.AuditEntries.ToListAsync());
        });
    }

    private static async Task<Guid> SellAsync(HttpClient client, TradingTenant tenant, object line)
    {
        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { line },
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> FirstLineIdAsync(HttpClient client, Guid saleId)
    {
        var sale = await client.GetFromJsonAsync<JsonElement>($"/api/v1/sales/{saleId}");

        return sale.GetProperty("lines")[0].GetProperty("id").GetGuid();
    }
}
