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
/// What a restaurant writes to the audit log, and — as much to the point — what it does not.
/// </summary>
/// <remarks>
/// The log is a targeted record of actions that cost money or hide theft, not a change feed. A
/// restaurant generates enormously more events than a counter does — every drink, every course,
/// every mind changed — and filing an entry for each of them would produce a log nobody reads,
/// which is the same as having none.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class OrderAuditTests(PosApiFactory factory)
{
    [Fact]
    public async Task Seating_a_table_is_audited_with_its_order_number()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.OrderOpened);

            Assert.Equal(orderId, entry.EntityId);

            // The order number, because that is what staff and a manager reading this back
            // actually have — nobody quotes a GUID across a dining room.
            Assert.Contains("orderNumber", entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Voiding_a_fired_line_is_audited_and_a_pending_one_is_not()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        var order = await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);
        var lineId = order.GetProperty("lines")[0].GetProperty("id").GetGuid();

        // Nobody has cooked anything: this is a customer changing their mind, and it costs the
        // shop nothing.
        using var pending = await client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/lines/{lineId}/void",
            new { reason = "Changed their mind" });

        Assert.Equal(HttpStatusCode.OK, pending.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Nothing. Filing an entry every time somebody unrings a drink is how a log stops
            // being read, and then the entries that matter are lost in it.
            Assert.Empty(await db.AuditEntries.Where(a => a.Action == AuditAction.OrderLineVoided).ToListAsync());
        });

        // Now one the kitchen has been told about.
        var second = await client.AddLineAsync(orderId, tenant.Catalog.CoffeeProductId);
        var firedId = second.GetProperty("lines")[1].GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var line = await db.OrderLines.FirstAsync(l => l.Id == firedId);

            line.Status = OrderLineStatus.Fired;
            line.FiredAt = DateTimeOffset.UtcNow;

            await db.SaveChangesAsync();
        });

        using var voided = await client.PostAsJsonAsync(
            $"/api/v1/orders/{orderId}/lines/{firedId}/void",
            new { reason = "Sent to the wrong table" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.OrderLineVoided);

            Assert.Equal(firedId, entry.EntityId);

            // The reason, because this is food the shop paid for and threw away, and "why" is
            // the entire question somebody asks about it a month later.
            Assert.Contains("Sent to the wrong table", entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Moving_an_order_to_another_table_is_audited_with_both_sides()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        using var moved = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/transfer",
            new { diningTableId = tenant.SecondTableId });

        Assert.Equal(HttpStatusCode.OK, moved.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.OrderTransferred);

            // Both sides. "Table 5 has a bill on it" is only explicable with the table it came
            // from, and a one-sided entry sends whoever is reconciling back to guessing.
            Assert.Contains(tenant.TableId.ToString(), entry.Before!, StringComparison.Ordinal);
            Assert.Contains(tenant.SecondTableId.ToString(), entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Absorbing_one_order_into_another_is_audited()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var target = await client.SeatAsync(tenant.TableId);
        var source = await client.SeatAsync(tenant.SecondTableId);

        await client.AddLineAsync(source, tenant.Catalog.WaterProductId);

        using var merged = await client.PostIdempotentAsync(
            $"/api/v1/orders/{target}/merge",
            new { sourceOrderId = source });

        Assert.Equal(HttpStatusCode.OK, merged.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.OrderMerged);

            Assert.Equal(target, entry.EntityId);
            Assert.Contains("absorbed", entry.After!, StringComparison.Ordinal);

            // The source is Closed, not Abandoned: nothing was written off, its items simply
            // live on the other order now. Marking it abandoned would report a loss that did
            // not happen.
            var absorbed = await db.Orders.FirstAsync(o => o.Id == source);
            Assert.Equal(OrderStatus.Closed, absorbed.Status);
        });
    }

    [Fact]
    public async Task Abandoning_an_order_records_the_reason()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        using var abandoned = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/abandon",
            new { reason = "Walked out" });

        Assert.Equal(HttpStatusCode.OK, abandoned.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.OrderAbandoned);

            Assert.Equal(orderId, entry.EntityId);
            Assert.Contains("Walked out", entry.After!, StringComparison.Ordinal);

            // Abandoned rather than Closed, so a report can tell a walk-out from a table that
            // paid. Closed means settled, and counting these as settled would claim takings
            // nobody received.
            var order = await db.Orders.FirstAsync(o => o.Id == orderId);
            Assert.Equal(OrderStatus.Abandoned, order.Status);
        });
    }

    [Fact]
    public async Task A_cashier_cannot_abandon_an_order()
    {
        // CanVoidFiredLine, not CanTakeOrders. Abandoning writes off whatever was cooked, which
        // is the same authority as voiding a fired line and for the same reason.
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId);

        using var cashier = await factory.CashierClientAsync(tenant);

        using var refused = await cashier.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/abandon",
            new { reason = "Attempt" });

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var order = await db.Orders.FirstAsync(o => o.Id == orderId);

            Assert.Equal(OrderStatus.Open, order.Status);
        });
    }
}
