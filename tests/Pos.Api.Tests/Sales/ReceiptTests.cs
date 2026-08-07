using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// <c>GET /sales/{id}/receipt</c> — the payload a customer's receipt is printed from.
/// </summary>
/// <remarks>
/// <see cref="Core.Tests"/>' <c>ReceiptBuilderTests</c> already pins the arithmetic against
/// random baskets. What is being tested here is everything the builder cannot see: that the
/// endpoint reads the sale's <b>snapshots</b> rather than the catalog, that the timestamp is
/// converted into the tenant's zone rather than served as UTC, and that a refund and a void
/// come back saying what they are.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ReceiptTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_receipt_carries_the_shops_header_block_the_tenant_configured()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await ConfigureShopAsync(tenant.TenantId);

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));
        var receipt = await ReceiptAsync(client, sale);

        var shop = receipt.GetProperty("shop");

        Assert.Equal("14 Harbour Road\nDún Laoghaire", shop.GetProperty("addressLine").GetString());
        Assert.Equal("IE1234567FA", shop.GetProperty("taxNumber").GetString());
        Assert.Equal("Open 7 days", shop.GetProperty("header").GetString());
        Assert.Equal("Returns within 30 days", shop.GetProperty("footer").GetString());
        Assert.Equal("EUR", shop.GetProperty("currencyCode").GetString());

        // Who and where, resolved from the current user and register rows.
        Assert.Equal("Ada Byrne", receipt.GetProperty("cashierName").GetString());
        Assert.Equal("Front Counter", receipt.GetProperty("registerName").GetString());
        Assert.Equal("Sale", receipt.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_shop_that_configured_nothing_still_gets_a_printable_receipt()
    {
        // The state every tenant is in on its first day. A receipt endpoint that needed the
        // optional fields would make a new shop unable to serve its first customer.
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        var receipt = await ReceiptAsync(client, sale);

        var shop = receipt.GetProperty("shop");

        Assert.Equal("Corner Shop", shop.GetProperty("name").GetString());
        Assert.Equal(JsonValueKind.Null, shop.GetProperty("addressLine").ValueKind);
        Assert.Equal(JsonValueKind.Null, shop.GetProperty("footer").ValueKind);
    }

    [Fact]
    public async Task The_timestamp_is_in_the_tenants_zone_and_not_in_utc()
    {
        // Australia/Sydney, because it is never at UTC+00:00 in either half of the year — a
        // Europe/Dublin tenant would pass this test for three months of winter regardless of
        // whether the conversion happened at all.
        var (client, tenant) = await factory.TradingTenantAsync();

        await ConfigureShopAsync(tenant.TenantId, timeZoneId: "Australia/Sydney");

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        var receipt = await ReceiptAsync(client, sale);

        var completedAtLocal = receipt.GetProperty("completedAtLocal").GetDateTimeOffset();
        var completedAtUtc = sale.GetProperty("completedAt").GetDateTimeOffset();

        Assert.Equal("Australia/Sydney", receipt.GetProperty("timeZoneId").GetString());

        // The same instant, told in local time. Both halves matter: an offset that is still
        // zero means no conversion happened, and an instant that moved means the conversion
        // changed the fact rather than how it is displayed.
        //
        // Compared with a tolerance, not for equality. The POST response carries the instant
        // .NET computed, at 100ns ticks; the receipt reads it back from timestamptz, which
        // holds microseconds — so the two differ in the last few ticks. A conversion that got
        // the zone wrong would be out by hours, which is what this is looking for.
        Assert.NotEqual(TimeSpan.Zero, completedAtLocal.Offset);
        Assert.True(
            (completedAtLocal.UtcDateTime - completedAtUtc.UtcDateTime).Duration()
                < TimeSpan.FromMilliseconds(1),
            $"The receipt's local time is {completedAtLocal:O}, which is not the same instant "
            + $"as the sale's {completedAtUtc:O}.");

        // The reprint stamp is in the same zone, for the same reason.
        Assert.NotEqual(TimeSpan.Zero, receipt.GetProperty("issuedAtLocal").GetDateTimeOffset().Offset);
    }

    [Fact]
    public async Task The_tax_breakdown_splits_by_rate_and_sums_to_the_sales_tax_total()
    {
        // Water at 23% and a carrier bag at 0%, so a single-rate breakdown is wrong for one of
        // them — the case §6.1 says is not derivable from one tax total.
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(
            client,
            tenant,
            (tenant.Catalog.WaterProductId, 3m),
            (tenant.Catalog.BagProductId, 2m));

        var receipt = await ReceiptAsync(client, sale);

        var breakdown = receipt.GetProperty("taxBreakdown").EnumerateArray().ToArray();

        Assert.Equal(2, breakdown.Length);
        Assert.Equal(0.0000m, breakdown[0].GetProperty("rate").GetDecimal());
        Assert.Equal(0.2300m, breakdown[1].GetProperty("rate").GetDecimal());

        Assert.Equal(
            receipt.GetProperty("taxTotal").GetDecimal(),
            breakdown.Sum(part => part.GetProperty("taxAmount").GetDecimal()));

        // And the receipt's own totals are the sale's, not a second opinion about them.
        Assert.Equal(sale.GetProperty("total").GetDecimal(), receipt.GetProperty("total").GetDecimal());
        Assert.Equal(sale.GetProperty("taxTotal").GetDecimal(), receipt.GetProperty("taxTotal").GetDecimal());
    }

    [Fact]
    public async Task A_receipt_prints_the_snapshot_even_after_the_product_is_renamed_and_repriced()
    {
        // Invariant 5, at the receipt. A shop that raises a price on Tuesday must not have
        // Monday's receipts quietly reprint at the new one — the reprint is the document a
        // customer brings back to dispute exactly that.
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.FirstAsync(p => p.Id == tenant.Catalog.WaterProductId);

            product.Name = "Sparkling Water 500ml";
            product.UnitPrice = (Money)9.9900m;

            await db.SaveChangesAsync();
        });

        var receipt = await ReceiptAsync(client, sale);
        var line = receipt.GetProperty("lines").EnumerateArray().Single();

        Assert.Equal(CatalogFixture.WaterName, line.GetProperty("description").GetString());
        Assert.Equal(1.2000m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(sale.GetProperty("total").GetDecimal(), receipt.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_refund_says_it_is_one_and_names_the_sale_it_reverses()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));

        using var refunded = await client.PostIdempotentAsync(
            $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/refund",
            new
            {
                clientTransactionId = Guid.CreateVersion7(),
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                reason = "Faulty",
            });

        Assert.Equal(HttpStatusCode.Created, refunded.StatusCode);

        var refund = await refunded.Content.ReadFromJsonAsync<JsonElement>();
        var receipt = await ReceiptAsync(client, refund);

        // Not "a sale with a minus sign". A customer handed −€2.95 and nothing else cannot tell
        // a refund from a sale that went wrong, and neither can whoever takes it back.
        Assert.Equal("Refund", receipt.GetProperty("kind").GetString());
        Assert.Equal("Faulty", receipt.GetProperty("refundReason").GetString());
        Assert.Equal(
            sale.GetProperty("saleNumber").GetInt64(),
            receipt.GetProperty("originalSaleNumber").GetInt64());
        Assert.Equal(
            sale.GetProperty("id").GetGuid(),
            receipt.GetProperty("originalSaleId").GetGuid());

        Assert.True(receipt.GetProperty("total").GetDecimal() < 0m);
    }

    [Fact]
    public async Task A_voided_sale_still_prints_and_says_it_was_voided()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var receipt = await ReceiptAsync(client, sale);

        // Printable, because a void is a record too — and unmistakable, because an unmarked
        // one could be presented as proof of purchase.
        Assert.Equal("VoidedSale", receipt.GetProperty("kind").GetString());
        Assert.Equal("Rung up twice", receipt.GetProperty("voidReason").GetString());
    }

    [Fact]
    public async Task A_sale_that_does_not_exist_is_a_404()
    {
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.GetAsync($"/api/v1/sales/{Guid.CreateVersion7()}/receipt");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- helpers -------------------------------------------------------------------

    /// <summary>
    /// Fills in the receipt fields, and optionally the zone.
    /// </summary>
    /// <remarks>
    /// Written straight to the row because there is no <c>PUT /settings</c>: these are
    /// onboarding values, and Phase 6 deliberately did not add a route for them.
    /// </remarks>
    private async Task ConfigureShopAsync(Guid tenantId, string? timeZoneId = null) =>
        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shop = await db.Tenants.FirstAsync(t => t.Id == tenantId);

            shop.AddressLine = "14 Harbour Road\nDún Laoghaire";
            shop.TaxNumber = "IE1234567FA";
            shop.ReceiptHeader = "Open 7 days";
            shop.ReceiptFooter = "Returns within 30 days";

            if (timeZoneId is not null)
            {
                shop.TimeZoneId = timeZoneId;
            }

            await db.SaveChangesAsync();
        });

    private static async Task<JsonElement> ReceiptAsync(HttpClient client, JsonElement sale)
    {
        using var response = await client.GetAsync(
            $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/receipt");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> SellAsync(
        HttpClient client,
        TradingTenant tenant,
        params (Guid ProductId, decimal Quantity)[] items)
    {
        var clientTransactionId = Guid.CreateVersion7();

        using var response = await client.PostIdempotentAsync(
            "/api/v1/sales",
            new
            {
                clientTransactionId,
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                lines = items.Select(i => new { productId = i.ProductId, quantity = i.Quantity }).ToArray(),

                // Comfortably over, so change is given and the receipt has something to show
                // for it. The server decides the amount either way.
                tenders = new[] { new { method = "Cash", amount = 100m } },
            },
            idempotencyKey: clientTransactionId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
