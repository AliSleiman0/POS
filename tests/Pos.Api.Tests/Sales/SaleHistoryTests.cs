using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Data;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// <c>GET /sales</c> as a history: filters, search, and the link between a refund and its
/// original in <b>both</b> directions.
/// </summary>
/// <remarks>
/// Its own tenant per test, like the other behavioural sale suites: these assert exact sets, and
/// against a shared shop "did the filter work?" answers "did another test ring something up?"
/// instead.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleHistoryTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_sale_is_found_by_the_number_printed_on_its_receipt()
    {
        // The only search that matters at a counter: the customer reads the number off the
        // paper in their hand.
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        var wanted = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));
        await SellAsync(client, tenant, (tenant.Catalog.BagProductId, 1m));

        var number = wanted.GetProperty("saleNumber").GetInt64();
        var found = await ListAsync(client, $"?saleNumber={number}");

        var only = Assert.Single(found);

        Assert.Equal(wanted.GetProperty("id").GetGuid(), only.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Filters_compose_rather_than_the_last_one_winning()
    {
        // Two at once, which is the case a handler that reassigned `query` instead of chaining
        // it would get wrong — and one filter each would never catch.
        var (client, tenant) = await factory.TradingTenantAsync();

        var kept = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        var voided = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/sales/{voided.GetProperty("id").GetGuid()}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Type and status together: completed sales only, no refunds and no voids.
        var found = await ListAsync(client, $"?type=Sale&status=Completed&shiftId={tenant.ShiftId}");

        var only = Assert.Single(found);

        Assert.Equal(kept.GetProperty("id").GetGuid(), only.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_date_range_is_a_trading_day_and_not_a_utc_one()
    {
        // The same boundary the daily report uses, and it has to be the same one: a history
        // that disagreed with the report by a few hours would make staff distrust both.
        var (client, tenant) = await factory.TradingTenantAsync();

        await ConfigureTradingDayAsync(tenant.TenantId, "Europe/Dublin", TimeSpan.FromHours(4));

        var evening = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        var afterMidnight = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));
        var nextDay = await SellAsync(client, tenant, (tenant.Catalog.BagProductId, 1m));

        // 23:30 and 01:30 local, both belonging to Friday the 12th; then 09:00 on Saturday.
        await BackdateAsync(tenant.TenantId, evening, new DateTimeOffset(2026, 6, 12, 22, 30, 0, TimeSpan.Zero));
        await BackdateAsync(tenant.TenantId, afterMidnight, new DateTimeOffset(2026, 6, 13, 0, 30, 0, TimeSpan.Zero));
        await BackdateAsync(tenant.TenantId, nextDay, new DateTimeOffset(2026, 6, 13, 8, 0, 0, TimeSpan.Zero));

        var friday = await ListAsync(client, "?from=2026-06-12&to=2026-06-12");

        Assert.Equal(2, friday.Count);
        Assert.DoesNotContain(friday, s => s.GetProperty("id").GetGuid() == nextDay.GetProperty("id").GetGuid());

        // Both ends inclusive: "12th to 13th" is two whole trading days, not one and a bit.
        Assert.Equal(3, (await ListAsync(client, "?from=2026-06-12&to=2026-06-13")).Count);
    }

    [Fact]
    public async Task A_filter_cannot_widen_the_scope_to_another_tenants_sales()
    {
        // The tenancy question a filter raises that the by-id theory cannot: naming another
        // shop's register must answer an empty page, not that shop's history.
        var (mine, tenant) = await factory.TradingTenantAsync();
        var (theirs, other) = await factory.TradingTenantAsync();

        await SellAsync(mine, tenant, (tenant.Catalog.WaterProductId, 1m));
        await SellAsync(theirs, other, (other.Catalog.WaterProductId, 1m));

        Assert.Empty(await ListAsync(mine, $"?registerId={other.RegisterId}"));
        Assert.Empty(await ListAsync(mine, $"?shiftId={other.ShiftId}"));
        Assert.Empty(await ListAsync(mine, $"?cashierId={other.OwnerId}"));

        // And its own history is still there — so the assertions above are not passing because
        // the endpoint returns nothing to anybody.
        Assert.Single(await ListAsync(mine, string.Empty));
    }

    [Fact]
    public async Task A_refund_and_its_original_point_at_each_other()
    {
        /*
         * §6.4's exit criterion, and the direction that gets forgotten is the second one. From
         * the refund you can always reach the original. From the *original*, without this, you
         * cannot see it was refunded — so a customer brings the same receipt back twice and the
         * shop pays out twice.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 2m));

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

        // Forwards.
        var refundDetail = await GetAsync(client, refund.GetProperty("id").GetGuid());

        Assert.Equal(
            sale.GetProperty("id").GetGuid(),
            refundDetail.GetProperty("originalSaleId").GetGuid());
        Assert.Equal(
            sale.GetProperty("saleNumber").GetInt64(),
            refundDetail.GetProperty("originalSaleNumber").GetInt64());
        Assert.Equal("Faulty", refundDetail.GetProperty("refundReason").GetString());

        // And backwards.
        var saleDetail = await GetAsync(client, sale.GetProperty("id").GetGuid());
        var linked = saleDetail.GetProperty("refunds").EnumerateArray().Single();

        Assert.Equal(refund.GetProperty("id").GetGuid(), linked.GetProperty("id").GetGuid());
        Assert.Equal(
            refund.GetProperty("total").GetDecimal(),
            linked.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_detail_view_names_the_cashier_the_till_and_a_void()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var detail = await GetAsync(client, sale.GetProperty("id").GetGuid());

        // A list of GUIDs is not a history anybody can read.
        Assert.Equal("Ada Byrne", detail.GetProperty("cashierName").GetString());
        Assert.Equal("Front Counter", detail.GetProperty("registerName").GetString());
        Assert.Equal("Rung up twice", detail.GetProperty("voidReason").GetString());
        Assert.Equal("Ada Byrne", detail.GetProperty("voidedByName").GetString());
        Assert.Equal("Voided", detail.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_voided_refund_no_longer_counts_against_its_original()
    {
        // A void reverses the refund, so the original is refundable again — and the detail must
        // say so, or a cashier reads "already refunded" and turns a customer away.
        var (client, tenant) = await factory.TradingTenantAsync();

        var sale = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));

        using var refunded = await client.PostIdempotentAsync(
            $"/api/v1/sales/{sale.GetProperty("id").GetGuid()}/refund",
            new
            {
                clientTransactionId = Guid.CreateVersion7(),
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                reason = "Faulty",
            });

        var refund = await refunded.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Single((await GetAsync(client, sale.GetProperty("id").GetGuid()))
            .GetProperty("refunds").EnumerateArray());

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{refund.GetProperty("id").GetGuid()}/void",
            new { reason = "Refunded in error" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        Assert.Empty((await GetAsync(client, sale.GetProperty("id").GetGuid()))
            .GetProperty("refunds").EnumerateArray());
    }

    [Theory]
    [InlineData("?type=Nonsense", "type")]
    [InlineData("?status=Nonsense", "status")]
    [InlineData("?from=last-tuesday", "from")]
    [InlineData("?to=2026-13-45", "to")]
    public async Task An_unreadable_filter_is_refused_rather_than_ignored(string query, string field)
    {
        // A filter that silently does nothing returns more history than was asked for, and
        // every row in it looks legitimate.
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.GetAsync(new Uri($"/api/v1/sales{query}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task The_history_starts_at_the_most_recent_sale()
    {
        /*
         * Unlike every other list in this API, which reads forwards.
         *
         * A history whose first page is the shop's very first sales is unusable at a counter —
         * the transaction anybody is looking for happened today, and after a year they would be
         * fifty pages back. The stock ledger's oldest-first rule answers a different question:
         * "why is this number wrong?" is read forwards.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));
        var newest = await SellAsync(client, tenant, (tenant.Catalog.BagProductId, 1m));

        var history = await ListAsync(client, string.Empty);

        Assert.Equal(
            newest.GetProperty("saleNumber").GetInt64(),
            history[0].GetProperty("saleNumber").GetInt64());

        // Strictly descending, not merely "the newest happens to be first".
        var numbers = history.Select(s => s.GetProperty("saleNumber").GetInt64()).ToArray();

        Assert.Equal([.. numbers.OrderDescending()], numbers);
    }

    [Fact]
    public async Task Paging_walks_a_long_history_seeing_every_sale_exactly_once()
    {
        // The property that matters most about a cursor, and the one a reversed order could
        // quietly break: a keyset predicate pointing the opposite way from the ORDER BY either
        // repeats a page for ever or returns nothing after the first.
        var (client, tenant) = await factory.TradingTenantAsync();

        var written = new List<Guid>();

        for (var index = 0; index < 7; index++)
        {
            written.Add((await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m)))
                .GetProperty("id").GetGuid());
        }

        var seen = new List<Guid>();
        string? cursor = null;

        do
        {
            var url = "/api/v1/sales?limit=2" + (cursor is null ? "" : $"&cursor={Uri.EscapeDataString(cursor)}");

            using var response = await client.GetAsync(new Uri(url, UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            seen.AddRange(body.GetProperty("items").EnumerateArray()
                .Select(item => item.GetProperty("id").GetGuid()));

            cursor = body.GetProperty("hasMore").GetBoolean()
                ? body.GetProperty("nextCursor").GetString()
                : null;
        } while (cursor is not null);

        Assert.Equal(written.Count, seen.Count);
        Assert.Equal([.. written.Order()], [.. seen.Order()]);
    }

    // ---- helpers -------------------------------------------------------------------

    private static async Task<List<JsonElement>> ListAsync(HttpClient client, string query)
    {
        using var response = await client.GetAsync(
            new Uri($"/api/v1/sales{query}", UriKind.Relative));

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET /api/v1/sales{query} answered {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());

        return [.. (await response.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("items")
            .EnumerateArray()];
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, Guid saleId)
    {
        using var response = await client.GetAsync(new Uri($"/api/v1/sales/{saleId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private Task ConfigureTradingDayAsync(Guid tenantId, string timeZoneId, TimeSpan dayStart) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shop = await db.Tenants.FirstAsync(t => t.Id == tenantId);

            shop.TimeZoneId = timeZoneId;
            shop.BusinessDayStartOffset = dayStart;

            await db.SaveChangesAsync();
        });

    /// <summary>
    /// Moves a sale's <c>CompletedAt</c>. A test may do this; the API may not, because a caller
    /// who could set it could backdate takings.
    /// </summary>
    private Task BackdateAsync(Guid tenantId, JsonElement sale, DateTimeOffset completedAt) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var id = sale.GetProperty("id").GetGuid();
            var row = await db.Sales.FirstAsync(s => s.Id == id);

            row.CompletedAt = completedAt;

            await db.SaveChangesAsync();
        });

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
                tenders = new[] { new { method = "Cash", amount = 500m } },
            },
            idempotencyKey: clientTransactionId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
