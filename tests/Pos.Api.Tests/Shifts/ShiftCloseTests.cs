using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Shifts;

/// <summary>
/// Closing a drawer: expected cash, the variance, and the race with a sale in flight.
/// </summary>
/// <remarks>
/// The variance is the single report an owner checks daily, which is why every input to it has
/// its own test here rather than one end-to-end assertion that would pass for several
/// different wrong reasons.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ShiftCloseTests(PosApiFactory factory)
{
    [Fact]
    public async Task Expected_cash_is_the_float_plus_what_the_drawer_actually_took()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        // Two units of water: 2.95 owed, a fiver tendered, 2.05 change. The drawer keeps 2.95,
        // not the 5.00 that was handed over.
        await SellAsync(client, tenant, quantity: 2m, tendered: 5m);

        var closed = await CloseAsync(client, tenant.ShiftId, countedCash: 102.95m);

        Assert.Equal(102.95m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("variance").GetDecimal());
    }

    [Fact]
    public async Task A_variance_is_counted_minus_expected_and_is_stored()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, quantity: 2m, tendered: 5m);

        // A fiver short.
        var closed = await CloseAsync(client, tenant.ShiftId, countedCash: 97.95m);

        Assert.Equal(102.95m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(-5m, closed.GetProperty("variance").GetDecimal());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.SingleAsync(s => s.Id == tenant.ShiftId);

            // Stored, not recomputed on read. Recomputing later would silently change a
            // historical variance whenever anything about the underlying sales changed.
            Assert.Equal(ShiftStatus.Closed, shift.Status);
            Assert.Equal((Pos.Core.Monetary.Money)(-5m), shift.Variance);
            Assert.Equal(tenant.OwnerId, shift.ClosedBy);
            Assert.NotNull(shift.ClosedAt);
        });
    }

    [Fact]
    public async Task Cash_movements_change_what_the_drawer_should_hold()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, quantity: 2m, tendered: 5m);

        using var drop = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/cash-movements",
            new { type = "Drop", amount = -50m, reason = "To the safe" });

        Assert.Equal(HttpStatusCode.Created, drop.StatusCode);

        var closed = await CloseAsync(client, tenant.ShiftId, countedCash: 52.95m);

        // 100 float + 2.95 taken - 50 dropped.
        Assert.Equal(52.95m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("variance").GetDecimal());
    }

    [Fact]
    public async Task A_refund_reduces_the_drawer()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m, tendered: 5m);

        Guid lineId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            lineId = (await db.SaleLines.SingleAsync(l => l.SaleId == saleId)).Id;
        });

        using var refunded = await client.PostIdempotentAsync($"/api/v1/sales/{saleId}/refund", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            reason = "Faulty",
            lines = new[] { new { saleLineId = lineId, quantity = 1m } },
        });

        Assert.Equal(HttpStatusCode.Created, refunded.StatusCode);

        // 100 + 2.95 taken - 1.48 paid back. The refund's tender is negative, so it subtracts
        // with no special case in the arithmetic.
        var closed = await CloseAsync(client, tenant.ShiftId, countedCash: 101.47m);

        Assert.Equal(101.47m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("variance").GetDecimal());
    }

    [Fact]
    public async Task Voiding_a_cash_sale_removes_it_from_expected_cash()
    {
        // A void hands the cash straight back, so it never stayed in the drawer. Excluded
        // rather than netted off: treating it as a sale plus a negative would give the same
        // total while making the report claim takings that did not happen.
        var (client, tenant) = await factory.TradingTenantAsync();

        var saleId = await SellAsync(client, tenant, quantity: 2m, tendered: 5m);

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{saleId}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var closed = await CloseAsync(client, tenant.ShiftId, countedCash: 100m);

        Assert.Equal(100m, closed.GetProperty("expectedCash").GetDecimal());
        Assert.Equal(0m, closed.GetProperty("variance").GetDecimal());
    }

    [Fact]
    public async Task Closing_a_closed_shift_is_refused_and_a_replayed_close_returns_the_original()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();
        var body = new { countedCash = 100m };
        var route = $"/api/v1/shifts/{tenant.ShiftId}/close";

        using var first = await client.PostIdempotentAsync(route, body, key);
        using var replay = await client.PostIdempotentAsync(route, body, key);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.True(replay.Headers.Contains("Idempotent-Replay"));

        // A different key is a second genuine attempt, and the drawer has already been counted.
        using var again = await client.PostIdempotentAsync(route, body);

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        var problem = await again.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://pos.example/errors/shift-closed", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_cash_movement_with_the_wrong_sign_for_its_type_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/cash-movements",
            new { type = "Drop", amount = 50m, reason = "To the safe" });

        await AssertFieldErrorAsync(response, "amount");

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            Assert.Empty(await db.CashMovements.ToListAsync());
        });
    }

    [Fact]
    public async Task A_cash_movement_with_no_reason_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/cash-movements",
            new { type = "Drop", amount = -50m, reason = "  " });

        await AssertFieldErrorAsync(response, "reason");
    }

    [Fact]
    public async Task A_cash_movement_against_a_closed_shift_is_refused()
    {
        // Its expected cash is already computed and stored. Accepting a movement afterwards
        // would leave a variance that no longer explains the drawer.
        var (client, tenant) = await factory.TradingTenantAsync();

        await CloseAsync(client, tenant.ShiftId, countedCash: 100m);

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/cash-movements",
            new { type = "Drop", amount = -50m, reason = "Too late" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_may_record_a_drop_but_not_close_the_drawer()
    {
        // CanSell versus CanCloseShift. A cashier who could close their own drawer could also
        // decide what it was supposed to contain.
        var (_, tenant) = await factory.TradingTenantAsync();

        await factory.CreateUserAsync(
            tenant.TenantId, "drawer@trading.test", TradingTenant.Password, RoleNames.Cashier, "Robin Vale");

        using var cashier = factory.CreateClient();
        cashier.WithBearer((await cashier.LoginAsync(
            tenant.Slug, "drawer@trading.test", TradingTenant.Password)).AccessToken);

        using var drop = await cashier.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/cash-movements",
            new { type = "Drop", amount = -50m, reason = "To the safe" });

        Assert.Equal(HttpStatusCode.Created, drop.StatusCode);

        using var close = await cashier.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/close",
            new { countedCash = 50m });

        Assert.Equal(HttpStatusCode.Forbidden, close.StatusCode);
    }

    [Fact]
    public async Task A_missing_counted_amount_is_refused_rather_than_read_as_zero()
    {
        // An omitted decimal binds to zero, and a drawer "counted" as empty produces a
        // variance equal to everything in it — which reads as theft.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/close",
            new { });

        await AssertFieldErrorAsync(response, "countedCash");
    }

    [Fact]
    public async Task A_sale_committing_while_a_shift_closes_is_either_counted_or_refused()
    {
        // The pair of locks, exercised. SaleWriter takes FOR SHARE on the shift before writing
        // anything; the close changes the status in the same statement that locks the row. So
        // either the sale commits first and this close counts it, or the sale blocks, finds
        // the shift closed and is refused.
        //
        // What must never happen is the third outcome: a sale that committed but was not
        // counted, which is a drawer short by exactly that sale with nothing to explain it.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var closer = factory.CreateClient();
        closer.WithBearer((await closer.LoginAsync(
            tenant.Slug, TradingTenant.OwnerEmail, TradingTenant.Password)).AccessToken);

        var sale = client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 2m } },
            tenders = new[] { new { method = "Cash", amount = 2.95m } },
        });

        var close = closer.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/close",
            new { countedCash = 102.95m });

        var responses = await Task.WhenAll(sale, close);

        Assert.DoesNotContain(HttpStatusCode.InternalServerError, responses.Select(r => r.StatusCode));

        var saleSucceeded = responses[0].StatusCode == HttpStatusCode.Created;

        Assert.True(
            saleSucceeded || responses[0].StatusCode == HttpStatusCode.Conflict,
            $"The sale answered {responses[0].StatusCode}, which is neither committed nor refused.");

        // The close itself must have succeeded either way — it holds the exclusive lock.
        Assert.Equal(HttpStatusCode.OK, responses[1].StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.SingleAsync(s => s.Id == tenant.ShiftId);

            var counted = (decimal)shift.ExpectedCash!.Value;

            // The whole point, stated as one assertion: whether the sale landed or not, the
            // expected cash agrees with what actually committed. 102.95 if it was counted,
            // 100.00 if it was refused — and never 100.00 with a committed sale behind it.
            Assert.Equal(saleSucceeded ? 102.95m : 100m, counted);

            Assert.Equal(saleSucceeded ? 1 : 0, await db.Sales.CountAsync());
        });

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    private static async Task<JsonElement> CloseAsync(HttpClient client, Guid shiftId, decimal countedCash)
    {
        using var response = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{shiftId}/close",
            new { countedCash });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<Guid> SellAsync(
        HttpClient client,
        TradingTenant tenant,
        decimal quantity,
        decimal tendered)
    {
        using var response = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity } },
            tenders = new[] { new { method = "Cash", amount = tendered } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty(field, out _), $"No '{field}' in errors.");
    }
}
