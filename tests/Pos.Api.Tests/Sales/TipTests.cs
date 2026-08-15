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
/// A tip through the whole path: the sale, the drawer, and the report that explains it.
/// </summary>
/// <remarks>
/// <b>The reconciliation being legible is the requirement, not merely balancing.</b> Expected
/// cash already contains the tips, because it sums tendered less change given and a tip is
/// over-tender that stayed in the drawer. Without a line of its own, a shop that took €40 in
/// tips reads as €40 over with nothing explaining it — the same failure as absorbing a
/// cash-rounding adjustment instead of recording it.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class TipTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_tip_is_recorded_beside_the_total_rather_than_inside_it()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 10m } },
            tip = 2m,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sale = await response.Content.ReadFromJsonAsync<JsonElement>();

        var total = sale.GetProperty("total").GetDecimal();
        var tip = sale.GetProperty("tipAmount").GetDecimal();
        var change = sale.GetProperty("changeGiven").GetDecimal();

        Assert.Equal(2m, tip);

        // Not folded into the total. Folding it in would inflate revenue, inflate the tax owed
        // on revenue nobody was charged tax for, and make a refund of the meal offer to hand
        // the gratuity back as well.
        Assert.Equal(10m - total - 2m, change);
        Assert.True(total < 10m);
    }

    [Fact]
    public async Task A_tender_that_does_not_cover_the_tip_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },

            // Enough for the water, nowhere near enough for a €50 tip.
            tenders = new[] { new { method = "Cash", amount = 2m } },
            tip = 50m,
        });

        // Refused rather than quietly reduced: silently trimming it would record a gratuity the
        // customer did not leave and a drawer that balanced against a lie.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Sales.ToListAsync());
        });
    }

    [Fact]
    public async Task A_negative_tip_is_refused_on_the_field()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 10m } },
            tip = -5m,
        });

        // A negative tip would take money out of the drawer with nothing explaining it.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("tip", out _));
    }

    [Fact]
    public async Task The_drawer_expects_the_tip_and_the_report_says_where_it_came_from()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var sold = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 10m } },
            tip = 2m,
        });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        var sale = await sold.Content.ReadFromJsonAsync<JsonElement>();
        var total = sale.GetProperty("total").GetDecimal();

        using var report = await client.GetAsync(
            new Uri($"/api/v1/shifts/{tenant.ShiftId}/report", UriKind.Relative));

        var body = await report.Content.ReadFromJsonAsync<JsonElement>();

        // The takings are the goods, and the tip is beside them.
        Assert.Equal(total, body.GetProperty("sales").GetProperty("total").GetDecimal());
        Assert.Equal(2m, body.GetProperty("sales").GetProperty("tips").GetDecimal());

        /*
         * And the drawer expects both: float + (tendered − change) = 100 + (10 − change).
         *
         * ShiftArithmetic is untouched by this feature, which is the point of taking the tip
         * out of the change rather than adding it to the total — the money arrives in the
         * expected figure by itself, and what the report adds is only the explanation.
         */
        var expected = body.GetProperty("cash").GetProperty("expected").GetDecimal();
        var change = sale.GetProperty("changeGiven").GetDecimal();

        Assert.Equal(100m + (10m - change), expected);

        // Which is the takings plus the tip, stated the other way round so the arithmetic a
        // manager does in their head is the one being asserted.
        Assert.Equal(100m + total + 2m, expected);
    }

    [Fact]
    public async Task A_sale_with_no_tip_reports_zero_and_behaves_as_before()
    {
        // Every counter sale. The field is optional and absent means none — unlike a shift's
        // countedCash, there is no reading of an absent tip that costs anybody money.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 10m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var sale = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0m, sale.GetProperty("tipAmount").GetDecimal());
        Assert.Equal(10m - sale.GetProperty("total").GetDecimal(), sale.GetProperty("changeGiven").GetDecimal());
    }

    [Fact]
    public async Task A_tip_cannot_be_attached_to_a_refund()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var sold = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 10m } },
            tip = 2m,
        });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        var saleId = (await sold.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        // `tip` is sent deliberately and there is no field to bind it to: RefundSaleRequest has
        // none. The assertion is that it cannot be smuggled in — a refund with a tip on it would
        // be the shop handing back the gratuity as well as the meal, which is the outcome
        // keeping the tip out of Total exists to prevent.
        using var refunded = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/refund", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            reason = "Changed their mind",
            tip = 2m,
        });

        Assert.Equal(HttpStatusCode.Created, refunded.StatusCode);

        var refund = await refunded.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(0m, refund.GetProperty("tipAmount").GetDecimal());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var row = await db.Sales.SingleAsync(sale => sale.Type == SaleType.Refund);

            // Zero on the row too, not merely omitted from the response. The refund path builds
            // its own Sale and never sets TipAmount, and this is what would notice if somebody
            // wired the request through to it later without thinking about what it means.
            Assert.Equal(Money.Zero, row.TipAmount);
        });
    }
}
