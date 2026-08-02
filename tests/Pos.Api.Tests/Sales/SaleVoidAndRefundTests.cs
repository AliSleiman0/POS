using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// Voids and refunds — corrections as new rows, never as edits.
/// </summary>
/// <remarks>
/// CLAUDE.md invariant 4. A completed sale keeps its amounts and its number forever; the only
/// mutation it ever receives is the four void columns, and even that writes compensating stock
/// movements rather than deleting the originals so the ledger keeps explaining itself.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleVoidAndRefundTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_void_returns_stock_to_its_prior_level()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, quantity: 4m);

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{sale}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Back to the seeded 12.
            var stock = await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.WaterProductId);
            Assert.Equal(CatalogFixture.WaterOnHand, stock.OnHand);

            // And by compensation, not deletion: the sale's -4 is still there, with a +4
            // beside it. The ledger still explains where the goods went and came back.
            var movements = await db.StockMovements
                .Where(m => m.SaleId == sale)
                .OrderBy(m => m.Quantity)
                .ToListAsync();

            Assert.Equal(2, movements.Count);
            Assert.Equal(StockMovementType.Sale, movements[0].Type);
            Assert.Equal(-4m, movements[0].Quantity);
            Assert.Equal(StockMovementType.Refund, movements[1].Type);
            Assert.Equal(4m, movements[1].Quantity);
        });
    }

    [Fact]
    public async Task A_voided_sales_amounts_and_number_are_byte_identical_afterwards()
    {
        // The guard on the one operation that legitimately updates a completed sale. Reading
        // every money column and the number before and after is what proves the void touched
        // only the four columns it is allowed to.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m);

        var before = await SnapshotAsync(tenant, saleId);

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/void",
            new { reason = "Customer changed their mind" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var after = await SnapshotAsync(tenant, saleId);

        Assert.Equal(before, after);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var sale = await db.Sales.SingleAsync(s => s.Id == saleId);

            Assert.Equal(SaleStatus.Voided, sale.Status);
            Assert.Equal("Customer changed their mind", sale.VoidReason);
            Assert.Equal(tenant.OwnerId, sale.VoidedBy);
            Assert.NotNull(sale.VoidedAt);
        });
    }

    [Fact]
    public void No_route_updates_or_deletes_a_sale()
    {
        // Enumerated from the routing table rather than grepped, so it stays true as routes
        // are added. A PUT or DELETE under /sales would be a way to rewrite a financial record
        // in place, which is the thing invariant 4 exists to prevent.
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        var offenders = source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? string.Empty)
                .Contains("/sales", StringComparison.OrdinalIgnoreCase))
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(m => $"{m} {e.RoutePattern.RawText}"))
            .Where(key => key.StartsWith("PUT", StringComparison.Ordinal)
                          || key.StartsWith("DELETE", StringComparison.Ordinal)
                          || key.StartsWith("PATCH", StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "A completed sale is append-only. Corrections are new linked rows and a status "
            + "flag, never an edit: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task A_double_void_is_refused_and_a_replayed_void_returns_the_original()
    {
        // Two different mechanisms, so both are tested. The same key is a retry and replays;
        // a different key is a second genuine attempt and is refused.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 1m);
        var key = Guid.CreateVersion7();
        var body = new { reason = "Duplicate" };

        using var first = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/void", body, key);
        using var replay = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/void", body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replay"));

        using var again = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/void", body);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var problem = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "https://pos.example/errors/sale-already-voided",
            problem.GetProperty("type").GetString());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // One compensating movement, not two. A second void would have put the goods back
            // twice and overstated the shop's stock.
            Assert.Single(await db.StockMovements
                .Where(m => m.SaleId == saleId && m.Type == StockMovementType.Refund)
                .ToListAsync());
        });
    }

    [Fact]
    public async Task A_void_with_no_reason_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 1m);

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/void",
            new { reason = "   " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_partial_refund_creates_a_new_linked_sale_with_negative_amounts()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 3m);
        var lineId = await FirstLineAsync(tenant, saleId);

        var refund = await RefundAsync(client, tenant, saleId, lineId, quantity: 1m);

        // One unit of water at 1.2000 + 23% = 1.48, negative.
        Assert.Equal(-1.48m, refund.GetProperty("total").GetDecimal());
        Assert.Equal("Refund", refund.GetProperty("type").GetString());

        var refundId = refund.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var row = await db.Sales.SingleAsync(s => s.Id == refundId);

            Assert.Equal(SaleType.Refund, row.Type);
            Assert.Equal(saleId, row.OriginalSaleId);

            // Its own number from the same counter, so an auditor can quote it.
            Assert.Equal(2, row.SaleNumber);

            // The original is untouched. A refund is a new row, never an edit.
            var original = await db.Sales.SingleAsync(s => s.Id == saleId);
            Assert.Equal(SaleStatus.Completed, original.Status);

            // Stock came back: 12 - 3 + 1.
            var stock = await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.WaterProductId);
            Assert.Equal(10.0000m, stock.OnHand);

            // The tender is negative and gives no change, so sum(Tender) == Total exactly.
            var tender = await db.Tenders.SingleAsync(t => t.SaleId == refundId);
            Assert.Equal((Money)(-1.48m), tender.Amount);
            Assert.Null(tender.ChangeGiven);
        });
    }

    [Fact]
    public async Task A_refund_is_priced_from_the_snapshot_not_the_current_catalog()
    {
        // The customer is owed what they paid. Re-pricing from the catalog would refund
        // today's price — more or less than they handed over, and it would look correct.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m);
        var lineId = await FirstLineAsync(tenant, saleId);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.SingleAsync(p => p.Id == tenant.Catalog.WaterProductId);

            product.UnitPrice = (Money)99.0000m;
            await db.SaveChangesAsync();
        });

        var refund = await RefundAsync(client, tenant, saleId, lineId, quantity: 1m);

        // 1.48, not 121.77.
        Assert.Equal(-1.48m, refund.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_partial_refund_of_a_discounted_line_returns_a_proportional_share_of_the_discount()
    {
        // One of three discounted items comes back, so a third of the discount does too.
        // Refunding the undiscounted price hands back more than was paid.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var sold = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 3m, discountAmount = 0.60m },
            },
            tenders = new[] { new { method = "Cash", amount = 10m } },
        });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        var sale = await sold.Content.ReadFromJsonAsync<JsonElement>();
        var saleId = sale.GetProperty("id").GetGuid();

        // 3 × 1.20 = 3.60, less 0.60 = 3.00 net, +23% = 3.69.
        Assert.Equal(3.69m, sale.GetProperty("total").GetDecimal());

        var lineId = await FirstLineAsync(tenant, saleId);
        var refund = await RefundAsync(client, tenant, saleId, lineId, quantity: 1m);

        // One unit: 1.20 less its 0.20 share of the discount = 1.00 net, +23% = 1.23.
        Assert.Equal(-1.23m, refund.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Refunding_more_than_remains_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m);
        var lineId = await FirstLineAsync(tenant, saleId);

        using var response = await PostRefundAsync(client, tenant, saleId, lineId, quantity: 3m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "https://pos.example/errors/refund-exceeds-original",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Two_partial_refunds_cannot_together_exceed_the_original()
    {
        // Sequentially, the second sees what the first already returned. The lock on the
        // original is what makes the same true when they arrive together.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m);
        var lineId = await FirstLineAsync(tenant, saleId);

        await RefundAsync(client, tenant, saleId, lineId, quantity: 1m);

        using var second = await PostRefundAsync(client, tenant, saleId, lineId, quantity: 1m);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        using var third = await PostRefundAsync(client, tenant, saleId, lineId, quantity: 1m);
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
    }

    [Fact]
    public async Task A_refund_with_no_lines_returns_everything_remaining()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m);

        using var response = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/refund", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            reason = "Faulty",
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var refund = await response.Content.ReadFromJsonAsync<JsonElement>();

        // 2.95, not 2.96 — and the difference is the round-once rule, visible.
        //
        // Two units in ONE refund is 2 × 1.2000 = 2.4000 net, +23% = 2.9520, rounded once to
        // 2.95. Two SEPARATE one-unit refunds are 1.48 each and come to 2.96. Both are
        // correct: each refund is its own amount a person is handed, rounded once, and a
        // customer who returns items on two visits genuinely receives a cent more than one
        // who returns them together.
        //
        // Asserting 2.96 here would have been asserting per-line rounding, which is exactly
        // what the engine refuses to do.
        Assert.Equal(-2.95m, refund.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_sale_that_has_been_refunded_cannot_be_voided()
    {
        // Voiding writes compensating movements for every line, and a refund has already put
        // some of them back. Doing both would return the same goods twice.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m);
        var lineId = await FirstLineAsync(tenant, saleId);

        await RefundAsync(client, tenant, saleId, lineId, quantity: 1m);

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/void",
            new { reason = "Too late" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "https://pos.example/errors/sale-already-refunded",
            problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Voiding_the_refund_makes_the_quantity_refundable_again()
    {
        // The other half of the rule above. A voided refund put the goods back on the
        // customer's side of the counter, so counting it against the remaining quantity would
        // refuse a refund the customer never actually received.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 1m);
        var lineId = await FirstLineAsync(tenant, saleId);

        var refund = await RefundAsync(client, tenant, saleId, lineId, quantity: 1m);
        var refundId = refund.GetProperty("id").GetGuid();

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{refundId}/void",
            new { reason = "Refunded in error" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        using var again = await PostRefundAsync(client, tenant, saleId, lineId, quantity: 1m);

        Assert.Equal(HttpStatusCode.Created, again.StatusCode);
    }

    [Fact]
    public async Task A_refunds_cash_leaves_the_current_shifts_drawer_not_the_originals()
    {
        // The money comes out of the drawer that is open now, which is where it physically is.
        // Charging it to a shift counted last Tuesday would make two days' variance wrong.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 1m);
        var lineId = await FirstLineAsync(tenant, saleId);

        Guid secondShiftId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.SingleAsync(s => s.Id == tenant.ShiftId);

            shift.Status = ShiftStatus.Closed;
            await db.SaveChangesAsync();
        });

        secondShiftId = await client.OpenShiftAsync(tenant.RegisterId);

        using var response = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/refund", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = secondShiftId,
            reason = "Faulty",
            lines = new[] { new { saleLineId = lineId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var refundId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var refund = await db.Sales.SingleAsync(s => s.Id == refundId);

            Assert.Equal(secondShiftId, refund.ShiftId);
            Assert.NotEqual(tenant.ShiftId, refund.ShiftId);
        });
    }

    [Fact]
    public async Task A_cashier_may_not_void_or_refund()
    {
        var (_, tenant) = await factory.TradingTenantAsync();

        await factory.CreateUserAsync(
            tenant.TenantId,
            "till@trading.test",
            TradingTenant.Password,
            Pos.Data.Identity.RoleNames.Cashier,
            "Robin Vale");

        using var cashier = factory.CreateClient();
        cashier.WithBearer((await cashier.LoginAsync(
            tenant.Slug, "till@trading.test", TradingTenant.Password)).AccessToken);

        var saleId = Guid.CreateVersion7();

        using var voided = await cashier.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/void", new { reason = "Attempt" });

        using var refunded = await cashier.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/refund", new { reason = "Attempt" });

        Assert.Equal(HttpStatusCode.Forbidden, voided.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, refunded.StatusCode);
    }

    private static Task<HttpResponseMessage> PostRefundAsync(
        HttpClient client,
        TradingTenant tenant,
        Guid saleId,
        Guid lineId,
        decimal quantity) =>
        client.PostIdempotentAsync($"/api/v1/sales/{saleId}/refund", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            reason = "Faulty",
            lines = new[] { new { saleLineId = lineId, quantity } },
        });

    private static async Task<JsonElement> RefundAsync(
        HttpClient client,
        TradingTenant tenant,
        Guid saleId,
        Guid lineId,
        decimal quantity)
    {
        using var response = await PostRefundAsync(client, tenant, saleId, lineId, quantity);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> SellAsync(HttpClient client, TradingTenant tenant, decimal quantity)
    {
        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity } },
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private async Task<Guid> FirstLineAsync(TradingTenant tenant, Guid saleId)
    {
        Guid lineId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            lineId = (await db.SaleLines.OrderBy(l => l.LineNumber).FirstAsync(l => l.SaleId == saleId)).Id;
        });

        return lineId;
    }

    /// <summary>Every money column and the number, as a comparable tuple.</summary>
    private async Task<string> SnapshotAsync(TradingTenant tenant, Guid saleId)
    {
        var snapshot = string.Empty;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var sale = await db.Sales.AsNoTracking().SingleAsync(s => s.Id == saleId);

            snapshot = string.Join(
                '|',
                sale.SaleNumber,
                sale.Subtotal,
                sale.DiscountTotal,
                sale.TaxTotal,
                sale.RoundingAdjustment,
                sale.Total,
                sale.CompletedAt.ToUnixTimeMilliseconds(),
                sale.ClientTransactionId,
                sale.ShiftId,
                sale.RegisterId,
                sale.CashierId,
                sale.TaxMode);
        });

        return snapshot;
    }
}
