using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Sales;
using Pos.Data;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// <c>POST /sales</c> with an <c>occurredAt</c> — a sale rung offline and sent when the
/// connection came back.
/// </summary>
/// <remarks>
/// The thing being protected here is not the column. It is that <b>"what did we take on
/// Tuesday" keeps meaning Tuesday</b> when the shop was offline on Tuesday evening: the
/// trading-day bounds, the shift's takings and the Z-report all read <c>CompletedAt</c>, and
/// dating a queued sale by the moment the network returned puts a day's money in the wrong day.
/// <para>
/// The second thing, and the one a client is most likely to get wrong, is the interaction with
/// idempotency: <c>occurredAt</c> is in the body, so it is in the fingerprint. A client that
/// re-reads its clock on each retry sends a different body under the same key and is refused
/// for ever — which at a till reads as a sale that simply will not go through.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class OfflineSaleTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/sales";

    private const string TimestampInvalid = "https://pos.example/errors/offline-sale-timestamp-invalid";

    [Fact]
    public async Task A_queued_sale_is_dated_when_the_customer_paid_not_when_it_arrived()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var paidAt = DateTimeOffset.UtcNow.AddHours(-6);

        var body = await SellAsync(client, tenant, occurredAt: paidAt);
        var saleId = body.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var sale = await db.Sales.SingleAsync(s => s.Id == saleId);

            // The trade's own moment, to the second. Every report groups by this column.
            Assert.Equal(paidAt.ToUnixTimeSeconds(), sale.CompletedAt.ToUnixTimeSeconds());

            // And the server's, which is now — this is what says the till was offline for six
            // hours rather than the shop having been open at a strange time.
            Assert.True(sale.RecordedAt > paidAt.AddHours(5));
            Assert.True(sale.RecordedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
        });
    }

    [Fact]
    public async Task An_ordinary_online_sale_has_both_timestamps_equal()
    {
        // No occurredAt at all: the two columns collapse to one value, so nothing downstream
        // has to special-case the common path and "was this offline?" answers itself.
        var (client, tenant) = await factory.TradingTenantAsync();

        var body = await SellAsync(client, tenant, occurredAt: null);
        var saleId = body.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var sale = await db.Sales.SingleAsync(s => s.Id == saleId);

            Assert.Equal(sale.CompletedAt, sale.RecordedAt);
        });
    }

    [Fact]
    public async Task The_response_carries_both_timestamps()
    {
        // The client tells an offline sale from an ordinary one by the gap between these two,
        // rather than by a flag it would have to be trusted to set.
        var (client, tenant) = await factory.TradingTenantAsync();

        var paidAt = DateTimeOffset.UtcNow.AddHours(-3);
        var body = await SellAsync(client, tenant, occurredAt: paidAt);

        Assert.Equal(
            paidAt.ToUnixTimeSeconds(),
            body.GetProperty("completedAt").GetDateTimeOffset().ToUnixTimeSeconds());

        Assert.True(body.GetProperty("recordedAt").GetDateTimeOffset() > paidAt.AddHours(2));
    }

    [Fact]
    public async Task A_sale_rung_yesterday_lands_in_yesterdays_history()
    {
        /*
         * The point of the whole feature, asserted through the filter a person actually uses.
         *
         * `?from=`/`?to=` are trading days resolved from CompletedAt, so a sale queued
         * yesterday evening and sent this morning has to appear under yesterday. Dated by
         * arrival it would appear under today, and the shop's own history would disagree with
         * the receipts in the till drawer.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        var yesterday = DateTimeOffset.UtcNow.AddDays(-1);

        await SellAsync(client, tenant, occurredAt: yesterday);

        /*
         * The day is resolved in the **tenant's** zone, not in UTC, and that is the whole
         * point of invariant 8 rather than a detail of this test.
         *
         * `?from=`/`?to=` are trading days in the shop's own calendar. Formatting the UTC date
         * instead worked for twenty-three hours a day and failed in the window where the two
         * calendars disagree — in summer, between 23:00 and midnight UTC, a sale at 23:51Z is
         * already tomorrow in Dublin. The test then asked for the wrong day and reported the
         * feature broken. Found at 23:51Z, which is the only reason it was found at all.
         */
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

        var day = TimeZoneInfo.ConvertTime(yesterday, zone)
            .ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        using var response = await client.GetAsync(
            new Uri($"{Route}?from={day}&to={day}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
    }

    [Fact]
    public async Task A_sale_dated_in_the_future_is_refused_permanently()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await PostAsync(
            client,
            tenant,
            occurredAt: DateTimeOffset.UtcNow.AddDays(1));

        // 422, not 400: the request is well-formed and every field is the right shape. What is
        // wrong is that the server does not believe the clock.
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(TimestampInvalid, body.GetProperty("type").GetString());

        // The extensions the review queue reads: what was claimed, and what the server thought
        // the time was. Without both, "the clock is wrong" cannot be shown to anybody.
        Assert.True(body.TryGetProperty("occurredAt", out _));
        Assert.True(body.TryGetProperty("serverTime", out _));

        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task A_sale_older_than_the_offline_window_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await PostAsync(
            client,
            tenant,
            occurredAt: DateTimeOffset.UtcNow - OfflineSaleRules.MaxOfflineWindow - TimeSpan.FromHours(1));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TimestampInvalid, body.GetProperty("type").GetString());

        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task A_bad_clock_is_reported_as_a_bad_clock_and_not_as_a_bad_cart()
    {
        /*
         * The sale names a product that does not exist *and* carries an impossible timestamp.
         * Only one answer can come back, and it has to be the clock: a till that has been
         * offline for a week will often have a stale catalog too, and telling its owner to go
         * and look at a product id sends them somewhere there is nothing to find.
         *
         * Falsified deliberately, and the result narrowed what this asserts. Moving the check
         * to sit *after* BuildCartAsync but still before the validation response does not
         * break it — both orders give the same answer, so the test is right not to fail. What
         * does break it is moving the check past the validation return, and that is exactly
         * the change that would make a till with a dead battery chase a phantom catalog bug.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = Guid.CreateVersion7(), quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 5m } },
            occurredAt = DateTimeOffset.UtcNow.AddDays(2),
        });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(TimestampInvalid, body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Replaying_the_same_key_with_the_same_occurred_at_returns_the_original_sale()
    {
        // What a correct outbox does: one timestamp minted when the sale completed, sent
        // unchanged on every attempt. The second attempt is a replay, not a second sale.
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();
        var clientTransactionId = Guid.CreateVersion7();
        var paidAt = DateTimeOffset.UtcNow.AddHours(-2);

        using var first = await PostAsync(client, tenant, paidAt, key, clientTransactionId);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await PostAsync(client, tenant, paidAt, key, clientTransactionId);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.True(second.Headers.Contains("Idempotent-Replay"));

        var firstBody = await first.Content.ReadFromJsonAsync<JsonElement>();
        var secondBody = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(firstBody.GetProperty("id").GetGuid(), secondBody.GetProperty("id").GetGuid());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Sales.ToListAsync());
        });
    }

    [Fact]
    public async Task Re_reading_the_clock_on_a_retry_is_refused_as_a_reused_key()
    {
        /*
         * The trap this feature introduces, pinned so nobody removes the warning from the DTO.
         *
         * `occurredAt` is part of the body, so it is part of the fingerprint. An outbox that
         * called Date.now() on each attempt would send a different body under the same key —
         * and the server, correctly, says the key already bought something else. At a till that
         * looks like a sale that will not go through, for ever, with no clue why.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();
        var clientTransactionId = Guid.CreateVersion7();

        using var first = await PostAsync(
            client, tenant, DateTimeOffset.UtcNow.AddHours(-2), key, clientTransactionId);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // One second later on the till's clock. Nothing else about the sale changed.
        using var second = await PostAsync(
            client, tenant, DateTimeOffset.UtcNow.AddHours(-2).AddSeconds(1), key, clientTransactionId);

        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);

        var body = await second.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(
            "https://pos.example/errors/idempotency-key-reused",
            body.GetProperty("type").GetString());

        // And exactly one sale exists, which is the part that matters: the customer was not
        // charged twice by the retry, they were refused.
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            Assert.Single(await db.Sales.ToListAsync());
        });
    }

    [Fact]
    public async Task A_replayed_offline_sale_still_moves_stock_once()
    {
        // Nothing about the timestamp changes the rest of the transaction. Asserted because a
        // second code path for offline sales is exactly what Phase 9 exists not to build.
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();
        var clientTransactionId = Guid.CreateVersion7();
        var paidAt = DateTimeOffset.UtcNow.AddHours(-1);

        using var first = await PostAsync(client, tenant, paidAt, key, clientTransactionId);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var second = await PostAsync(client, tenant, paidAt, key, clientTransactionId);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Water started at 12 and one was sold, once.
            var stock = await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.WaterProductId);
            Assert.Equal(11.0000m, stock.OnHand);

            Assert.Single(await db.StockMovements.ToListAsync());
        });
    }

    [Fact]
    public async Task The_idempotency_record_is_stamped_when_the_server_answered()
    {
        /*
         * Not with the trade's time. The record describes the attempt — "this key was answered
         * with this body" — and for a sale queued six hours ago the trade's time has nothing to
         * do with when the server answered. Stamping it with CompletedAt would age the record
         * against a clock it has no relationship with.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();
        var paidAt = DateTimeOffset.UtcNow.AddHours(-6);

        using var response = await PostAsync(client, tenant, paidAt, key);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var record = await db.IdempotencyRecords.SingleAsync(r => r.Key == key);

            Assert.True(
                record.CompletedAt > paidAt.AddHours(5),
                $"The record was stamped {record.CompletedAt:O}, which is the trade's time rather than the server's.");
        });
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        TradingTenant tenant,
        DateTimeOffset? occurredAt,
        Guid? idempotencyKey = null,
        Guid? clientTransactionId = null) =>
        client.PostIdempotentAsync(
            Route,
            new
            {
                clientTransactionId = clientTransactionId ?? Guid.CreateVersion7(),
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
                tenders = new[] { new { method = "Cash", amount = 5m } },
                occurredAt,
            },
            idempotencyKey);

    private static async Task<JsonElement> SellAsync(
        HttpClient client,
        TradingTenant tenant,
        DateTimeOffset? occurredAt)
    {
        using var response = await PostAsync(client, tenant, occurredAt);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AssertNothingCommittedAsync(TradingTenant tenant) =>
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Empty(await db.StockMovements.ToListAsync());
        });
}
