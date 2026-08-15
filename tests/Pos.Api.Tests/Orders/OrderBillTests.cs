using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Tests.Orders;

/// <summary>
/// Bills, and the claim the whole phase rests on: a settled bill is an ordinary sale.
/// </summary>
/// <remarks>
/// If these pass, "restaurant mode is a separate model, not a bolt-on to <c>Sale</c>" and "there
/// is no second money path" are both true at once — the order is its own shape, and the money
/// goes through <c>ISaleWriter</c>, the pricing engine, the stock ledger and the Z-report exactly
/// as a counter sale does.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class OrderBillTests(PosApiFactory factory)
{
    [Fact]
    public async Task Paying_a_bill_writes_an_ordinary_sale_with_lines_tenders_and_stock()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, quantity: 2m);

        var bill = await CreateBillAsync(client, orderId);
        var total = bill.GetProperty("total").GetDecimal();

        Assert.True(total > 0m);

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{bill.GetProperty("id").GetGuid()}/pay",
            new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = total } } });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // One sale, and it is an ordinary one: it has a sale number from the same counter,
            // lines, a tender and a stock movement. Nothing here is restaurant-shaped.
            var sale = Assert.Single(await db.Sales.ToListAsync());

            Assert.Equal(SaleType.Sale, sale.Type);
            Assert.Equal(SaleStatus.Completed, sale.Status);
            Assert.True(sale.SaleNumber > 0);
            Assert.Equal(total, (decimal)sale.Total);

            Assert.Single(await db.SaleLines.Where(l => l.SaleId == sale.Id).ToListAsync());
            Assert.Single(await db.Tenders.Where(t => t.SaleId == sale.Id).ToListAsync());

            var movement = await db.StockMovements.SingleAsync(m => m.SaleId == sale.Id);
            Assert.Equal(StockMovementType.Sale, movement.Type);
            Assert.Equal(-2m, movement.Quantity);

            // And the arrow points one way: the bill knows its sale, the sale knows nothing
            // about any order. That is what keeps the retail path unaware this exists.
            var stored = await db.OrderBills.SingleAsync();
            Assert.Equal(sale.Id, stored.SaleId);
            Assert.Equal(OrderBillStatus.Paid, stored.Status);
        });
    }

    [Fact]
    public async Task An_order_closes_only_when_every_line_is_billed_and_every_bill_is_paid()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);
        await client.AddLineAsync(orderId, tenant.Catalog.CoffeeProductId);

        var waterLineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();

        // Bill only the water.
        var first = await CreateBillAsync(client, orderId, [(waterLineId, 1m)]);

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{first.GetProperty("id").GetGuid()}/pay",
            new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = first.GetProperty("total").GetDecimal() } } });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Still open. An order closed with a line nobody paid for is food given away that
            // no report would ever show.
            Assert.Equal(OrderStatus.Open, (await db.Orders.SingleAsync()).Status);
        });

        // Now the coffee.
        var second = await CreateBillAsync(client, orderId);

        using var settled = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{second.GetProperty("id").GetGuid()}/pay",
            new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = second.GetProperty("total").GetDecimal() } } });

        Assert.Equal(HttpStatusCode.OK, settled.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Equal(OrderStatus.Closed, (await db.Orders.SingleAsync()).Status);

            // Two sales from one table, which is what splitting by item means.
            Assert.Equal(2, await db.Sales.CountAsync());
        });
    }

    [Fact]
    public async Task Allocating_more_than_a_line_has_left_is_refused()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, quantity: 2m);
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();

        await CreateBillAsync(client, orderId, [(lineId, 1.5m)]);

        using var refused = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills",
            new { allocations = new[] { new { orderLineId = lineId, quantity = 1m } } });

        // Refused rather than clamped: billing three of two is somebody having split the table
        // wrong, and silently trimming it would charge for two while the screen said three.
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);

        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("allocations[0].quantity", out _));
    }

    [Fact]
    public async Task A_shared_bottle_splits_its_discount_with_its_quantity()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        using var added = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new
            {
                lines = new[]
                {
                    new
                    {
                        productId = tenant.Catalog.WaterProductId,
                        quantity = 2m,
                        discountAmount = 1.00m,
                    },
                },
            });

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);

        var lineId = (await added.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("lines")[0].GetProperty("id").GetGuid();

        var half = await CreateBillAsync(client, orderId, [(lineId, 1m)]);
        var other = await CreateBillAsync(client, orderId, [(lineId, 1m)]);

        // Half the line takes half the discount, so the two bills still sum to what the whole
        // line would have cost. Independently rounding a share is the penny-off bug one level
        // up, which is why the share is computed the way RefundRules does it.
        Assert.Equal(
            half.GetProperty("total").GetDecimal(),
            other.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Paying_twice_with_one_key_charges_once()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var bill = await CreateBillAsync(client, orderId);
        var billId = bill.GetProperty("id").GetGuid();
        var total = bill.GetProperty("total").GetDecimal();

        var key = Guid.CreateVersion7();
        var body = new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = total } } };

        using var first = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/bills/{billId}/pay", body, key);
        using var second = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/bills/{billId}/pay", body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Exactly one. A table double-tapped at the end of an evening must not be charged
            // twice, and the bill's key — minted with the bill, not with the attempt — is what
            // makes the retry the same work.
            Assert.Single(await db.Sales.ToListAsync());
        });
    }

    [Fact]
    public async Task A_bill_takes_the_price_the_guest_was_quoted_not_the_price_now()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var quoted = order.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal();

        // 19:00, and the menu changes.
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.FirstAsync(p => p.Id == tenant.Catalog.WaterProductId);

            product.UnitPrice = (Core.Monetary.Money)99m;
            await db.SaveChangesAsync();
        });

        var bill = await CreateBillAsync(client, orderId);

        // The bill is built from the order line's snapshots, never from the catalog — the same
        // rule a refund follows when it re-prices from a sale line.
        Assert.Equal(quoted, bill.GetProperty("lines")[0].GetProperty("unitPrice").GetDecimal());
    }

    [Fact]
    public async Task An_even_split_is_several_tenders_on_one_bill()
    {
        /*
         * The decision, exercised rather than described.
         *
         * Four people paying a quarter each is four cash tenders against one sale — which
         * Tender has supported since Phase 3. Allocating a quarter of every line to four bills
         * would put fractional quantities through four independent pricings, and four
         * independently rounded parts do not sum to the whole.
         */
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId, covers: 4);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, quantity: 4m);

        var bill = await CreateBillAsync(client, orderId);
        var total = bill.GetProperty("total").GetDecimal();

        var share = Math.Round(total / 4m, 2, MidpointRounding.AwayFromZero);

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{bill.GetProperty("id").GetGuid()}/pay",
            new
            {
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                tenders = new[]
                {
                    new { amount = share },
                    new { amount = share },
                    new { amount = share },

                    // The last person covers whatever the rounding left, which is what happens
                    // at a table and is why this is a tender problem rather than a bill one.
                    new { amount = total - (share * 3m) },
                },
            });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var sale = Assert.Single(await db.Sales.ToListAsync());

            Assert.Equal(4, await db.Tenders.CountAsync(t => t.SaleId == sale.Id));
            Assert.Equal(total, (decimal)sale.Total);
        });
    }

    [Fact]
    public async Task A_tip_on_a_bill_reaches_the_sale_and_the_drawer()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var bill = await CreateBillAsync(client, orderId);
        var total = bill.GetProperty("total").GetDecimal();

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{bill.GetProperty("id").GetGuid()}/pay",
            new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = total + 5m } }, tip = 5m });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var sale = Assert.Single(await db.Sales.ToListAsync());

            Assert.Equal(5m, (decimal)sale.TipAmount);
            Assert.Equal(total, (decimal)sale.Total);

            // Nothing back: the tip is the over-tender that stayed in the drawer.
            var tender = await db.Tenders.SingleAsync(t => t.SaleId == sale.Id);
            Assert.True(tender.ChangeGiven is null || tender.ChangeGiven.Value.IsZero);
        });
    }

    [Fact]
    public async Task A_bill_cannot_be_torn_up_once_it_is_paid()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var bill = await CreateBillAsync(client, orderId);
        var billId = bill.GetProperty("id").GetGuid();

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{billId}/pay",
            new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = bill.GetProperty("total").GetDecimal() } } });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        using var torn = await client.DeleteAsync(
            new Uri($"/api/v1/orders/{orderId}/bills/{billId}", UriKind.Relative));

        // A paid bill is a completed sale, and invariant 4 says those are never undone by
        // deletion. Reversing one is a refund, which Phase 3.7 already owns.
        Assert.Equal(HttpStatusCode.Conflict, torn.StatusCode);
    }

    [Fact]
    public async Task An_unpaid_bill_can_be_torn_up_and_its_lines_split_again()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, quantity: 2m);

        var bill = await CreateBillAsync(client, orderId);

        using var torn = await client.DeleteAsync(
            new Uri($"/api/v1/orders/{orderId}/bills/{bill.GetProperty("id").GetGuid()}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, torn.StatusCode);

        // The whole line is unbilled again, so a different split is possible.
        var second = await CreateBillAsync(client, orderId);

        Assert.Equal(2m, second.GetProperty("lines")[0].GetProperty("quantity").GetDecimal());

        // And the bill number moved on rather than being reused: "bill two" has to mean one
        // thing at a table for the length of an evening.
        Assert.Equal(2, second.GetProperty("billNumber").GetInt32());
    }

    [Fact]
    public async Task A_tender_that_does_not_cover_the_bill_is_refused()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var bill = await CreateBillAsync(client, orderId);

        using var refused = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{bill.GetProperty("id").GetGuid()}/pay",
            new { registerId = tenant.RegisterId, shiftId = tenant.ShiftId, tenders = new[] { new { amount = 0.01m } } });

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Equal(OrderBillStatus.Open, (await db.OrderBills.SingleAsync()).Status);
        });
    }

    private static async Task<JsonElement> CreateBillAsync(
        HttpClient client,
        Guid orderId,
        IReadOnlyList<(Guid LineId, decimal Quantity)>? allocations = null)
    {
        object body = allocations is null
            ? new { }
            : new
            {
                allocations = allocations
                    .Select(a => new { orderLineId = a.LineId, quantity = a.Quantity })
                    .ToArray(),
            };

        using var response = await client.PostIdempotentAsync($"/api/v1/orders/{orderId}/bills", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
