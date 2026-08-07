using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Reports;

/// <summary>
/// The Z-report and the daily report.
/// </summary>
/// <remarks>
/// §6.3's bar, and it is a specific one: <b>the report has to reconcile against sales this test
/// actually made</b>, not against a fixture that agrees with it. Every sale here goes through
/// <c>POST /sales</c>, so a report that disagreed with the endpoints would fail rather than
/// producing a self-consistent wrong answer.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ReportTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_shifts_report_reconciles_with_the_sales_that_were_rung_through_it()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        // Three baskets across two tax rates, so the breakdown has something to get wrong.
        var first = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));
        var second = await SellAsync(client, tenant, (tenant.Catalog.BagProductId, 3m));
        var third = await SellAsync(
            client,
            tenant,
            (tenant.Catalog.WaterProductId, 1m),
            (tenant.Catalog.CoffeeProductId, 2m));

        var report = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");
        var sales = report.GetProperty("sales");

        Assert.Equal(3, sales.GetProperty("transactionCount").GetInt32());

        // The total, against the sales themselves rather than against a number this test
        // worked out — which is what makes this a reconciliation and not a restatement.
        var expectedTotal = Total(first) + Total(second) + Total(third);
        Assert.Equal(expectedTotal, sales.GetProperty("total").GetDecimal());

        // The chain a person checks by eye, and both halves have to hold exactly.
        Assert.Equal(
            sales.GetProperty("net").GetDecimal(),
            sales.GetProperty("gross").GetDecimal() - sales.GetProperty("discounts").GetDecimal());

        Assert.Equal(
            sales.GetProperty("total").GetDecimal(),
            sales.GetProperty("net").GetDecimal()
                + sales.GetProperty("tax").GetDecimal()
                + sales.GetProperty("rounding").GetDecimal());

        /*
         * Two rates, and the parts sum to the headline tax.
         *
         * This is the assertion that found the report's version of the receipt bug: the header
         * is rounded once per sale and the lines are stored at four places, so summing three
         * sales' headers gave 2.9000 while summing their lines gave 2.8980. Both are defensible
         * numbers on their own, and a report that showed them side by side would be queried by
         * the first accountant to read it.
         */
        var breakdown = report.GetProperty("taxByRate").EnumerateArray().ToArray();

        Assert.Equal(2, breakdown.Length);
        Assert.Equal(
            sales.GetProperty("tax").GetDecimal(),
            breakdown.Sum(part => part.GetProperty("tax").GetDecimal()));

        // And the taxable base, on the same reasoning.
        Assert.Equal(
            sales.GetProperty("net").GetDecimal(),
            breakdown.Sum(part => part.GetProperty("net").GetDecimal()));

        // And the average basket is the total over the count, not over something else.
        Assert.Equal(
            decimal.Round(expectedTotal / 3m, 2, MidpointRounding.AwayFromZero),
            sales.GetProperty("averageBasket").GetDecimal());
    }

    [Fact]
    public async Task A_voided_sale_is_left_out_of_the_takings_and_listed_with_its_actor_and_reason()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var kept = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));
        var scrapped = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));

        using var voided = await client.PostIdempotentAsync(
            $"/api/v1/sales/{scrapped.GetProperty("id").GetGuid()}/void",
            new { reason = "Rung up twice" });

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        var report = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");

        // Excluded, not subtracted. A void hands the cash straight back, so the money never
        // stayed in the drawer; netting it off would give the same total while claiming
        // takings that did not happen.
        Assert.Equal(1, report.GetProperty("sales").GetProperty("transactionCount").GetInt32());
        Assert.Equal(Total(kept), report.GetProperty("sales").GetProperty("total").GetDecimal());

        // Listed, because a void that vanished from the report is the one nobody investigates.
        var listed = report.GetProperty("voids").EnumerateArray().Single();

        Assert.Equal(
            scrapped.GetProperty("saleNumber").GetInt64(),
            listed.GetProperty("saleNumber").GetInt64());
        Assert.Equal("Rung up twice", listed.GetProperty("reason").GetString());
        Assert.Equal("Ada Byrne", listed.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_refund_reduces_the_takings_and_is_listed_against_its_original()
    {
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
        var report = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");
        var sales = report.GetProperty("sales");

        // Sold and given back: the day took nothing.
        Assert.Equal(0m, sales.GetProperty("total").GetDecimal());
        Assert.Equal(Total(refund), sales.GetProperty("refundTotal").GetDecimal());
        Assert.Equal(1, sales.GetProperty("refundCount").GetInt32());

        var listed = report.GetProperty("refunds").EnumerateArray().Single();

        Assert.Equal("Faulty", listed.GetProperty("reason").GetString());
        Assert.Equal(
            sale.GetProperty("saleNumber").GetInt64(),
            listed.GetProperty("originalSaleNumber").GetInt64());
    }

    [Fact]
    public async Task Cash_reconciles_across_sales_refunds_drops_and_payouts()
    {
        // The exit criterion, exercised with all four moving parts at once — the combination is
        // the point, because each on its own is easy and the signs are what get confused.
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 4m));

        var refunded = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));

        using var refund = await client.PostIdempotentAsync(
            $"/api/v1/sales/{refunded.GetProperty("id").GetGuid()}/refund",
            new
            {
                clientTransactionId = Guid.CreateVersion7(),
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                reason = "Changed their mind",
            });

        Assert.Equal(HttpStatusCode.Created, refund.StatusCode);

        // Money out of the drawer, twice, with the sign rule the endpoint enforces.
        await CashMovementAsync(client, tenant.ShiftId, "Drop", -50m, "To the safe");
        await CashMovementAsync(client, tenant.ShiftId, "Payout", -12.50m, "Window cleaner");

        var report = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");
        var cash = report.GetProperty("cash");

        // Still open, so the figure is computed live and says so. Nothing has been counted.
        Assert.True(cash.GetProperty("isProvisional").GetBoolean());
        Assert.Equal(JsonValueKind.Null, cash.GetProperty("counted").ValueKind);
        Assert.Equal(JsonValueKind.Null, cash.GetProperty("variance").ValueKind);

        Assert.Equal(-62.50m, cash.GetProperty("cashMovements").GetDecimal());
        Assert.Equal(2, cash.GetProperty("movements").GetArrayLength());

        // float + what came in over the counter + what left it.
        Assert.Equal(
            cash.GetProperty("openingFloat").GetDecimal()
                + cash.GetProperty("cashSales").GetDecimal()
                + cash.GetProperty("cashRefunds").GetDecimal()
                + cash.GetProperty("cashMovements").GetDecimal(),
            cash.GetProperty("expected").GetDecimal());

        // Now close it, and the report must switch to the stored reconciliation rather than
        // recomputing one — the rule Shift.ExpectedCash exists to state.
        var counted = cash.GetProperty("expected").GetDecimal() - 5m;

        using var closed = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/close",
            new { countedCash = counted });

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        var after = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");
        var reconciled = after.GetProperty("cash");

        Assert.False(reconciled.GetProperty("isProvisional").GetBoolean());
        Assert.Equal(counted, reconciled.GetProperty("counted").GetDecimal());

        // Five short, and the sign says short rather than over.
        Assert.Equal(-5m, reconciled.GetProperty("variance").GetDecimal());
    }

    [Fact]
    public async Task Cash_rounding_shows_up_in_the_report_and_reconciles()
    {
        /*
         * §6.3's last unchecked line: the rounding-adjustment total has to reconcile, because an
         * unexplained one means the rule is being applied inconsistently somewhere.
         *
         * Every other test here runs a tenant with no rounding increment, so `rounding` was
         * zero and the identity held trivially. A 5c shop is where it can actually fail — and
         * the adjustment is recorded rather than absorbed precisely so the drawer is not over
         * or short by an amount nothing explains.
         */
        var (client, tenant) = await factory.TradingTenantAsync(cashRoundingIncrement: 0.05m);

        // Three baskets whose totals do not land on a 5c boundary: 1 × 1.20 @ 23% is 1.476,
        // and the awkward cents are the point.
        var first = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 1m));
        var second = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 3m));
        var third = await SellAsync(client, tenant, (tenant.Catalog.BagProductId, 7m));

        var report = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");
        var sales = report.GetProperty("sales");

        // Non-zero, or this test is passing for the same trivial reason as the others.
        var rounding = sales.GetProperty("rounding").GetDecimal();
        Assert.NotEqual(0m, rounding);

        // It is the sum of what the sales themselves recorded, not a figure the report invented.
        Assert.Equal(
            first.GetProperty("roundingAdjustment").GetDecimal()
                + second.GetProperty("roundingAdjustment").GetDecimal()
                + third.GetProperty("roundingAdjustment").GetDecimal(),
            rounding);

        // And the headline identity still closes with it in place.
        Assert.Equal(
            sales.GetProperty("total").GetDecimal(),
            sales.GetProperty("net").GetDecimal() + sales.GetProperty("tax").GetDecimal() + rounding);

        // The tax breakdown reconciles against the taxable base, which cash rounding does not
        // touch — it moves the payable total, never the tax owed.
        var breakdown = report.GetProperty("taxByRate").EnumerateArray().ToArray();

        Assert.Equal(
            sales.GetProperty("tax").GetDecimal(),
            breakdown.Sum(part => part.GetProperty("tax").GetDecimal()));

        // Every cash total is a multiple of 5c, which is the rule the adjustment exists to keep.
        foreach (var sale in new[] { first, second, third })
        {
            var total = sale.GetProperty("total").GetDecimal();

            Assert.Equal(0m, total % 0.05m);
        }
    }

    [Fact]
    public async Task A_day_with_no_sales_is_zeroes_rather_than_an_error()
    {
        // A shop that opened and sold nothing still has to cash up against its float, and a
        // 404 would leave it nothing to do that with.
        var (client, _) = await factory.TradingTenantAsync();

        var report = await ReportAsync(client, "/api/v1/reports/daily?date=2020-01-01");
        var sales = report.GetProperty("sales");

        Assert.Equal(0, sales.GetProperty("transactionCount").GetInt32());
        Assert.Equal(0m, sales.GetProperty("total").GetDecimal());
        Assert.Equal(0m, sales.GetProperty("averageBasket").GetDecimal());
        Assert.Empty(report.GetProperty("taxByRate").EnumerateArray());
        Assert.Empty(report.GetProperty("voids").EnumerateArray());
    }

    [Fact]
    public async Task A_sale_before_and_after_the_day_start_lands_on_the_day_the_staff_would_say()
    {
        // §6.3's stated test: a 23:30 and a 01:30 sale under a tenant with a 04:00 day start.
        // Both belong to the earlier trading day, and a UTC-day report would split them.
        var (client, tenant) = await factory.TradingTenantAsync();

        await ConfigureTradingDayAsync(tenant.TenantId, "Europe/Dublin", TimeSpan.FromHours(4));

        var lateEvening = new DateTimeOffset(2026, 6, 12, 22, 30, 0, TimeSpan.Zero);   // 23:30 local
        var earlyMorning = new DateTimeOffset(2026, 6, 13, 0, 30, 0, TimeSpan.Zero);   // 01:30 local

        var evening = await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));
        var morning = await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 1m));

        await BackdateAsync(tenant.TenantId, evening, lateEvening);
        await BackdateAsync(tenant.TenantId, morning, earlyMorning);

        // Friday the 12th: both of them, because the 01:30 sale was rung by whoever was still
        // closing up on Friday night.
        var friday = await ReportAsync(client, "/api/v1/reports/daily?date=2026-06-12");

        Assert.Equal(2, friday.GetProperty("sales").GetProperty("transactionCount").GetInt32());
        Assert.Equal(
            Total(evening) + Total(morning),
            friday.GetProperty("sales").GetProperty("total").GetDecimal());

        // And Saturday has neither.
        var saturday = await ReportAsync(client, "/api/v1/reports/daily?date=2026-06-13");

        Assert.Equal(0, saturday.GetProperty("sales").GetProperty("transactionCount").GetInt32());
    }

    [Fact]
    public async Task A_drawer_opened_yesterday_and_still_open_is_on_todays_report()
    {
        /*
         * Scoping shifts on the day they opened is the obvious thing and it is wrong. An
         * overnight drawer's sales fall in today's window, so a report that left the shift out
         * showed a day of cash takings with no opening float behind them and no shift to say
         * the drawer had not been counted — which reads as fully reconciled.
         *
         * Found end to end rather than here: `pos_e2e` is seeded and never dropped, so its
         * drawer has been open since the first run and every report after midnight was quietly
         * missing it.
         */
        var (client, tenant) = await factory.TradingTenantAsync();

        await BackdateShiftAsync(tenant.TenantId, tenant.ShiftId, DateTimeOffset.UtcNow.AddDays(-1));

        await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 2m));

        var report = await ReportAsync(client, "/api/v1/reports/daily");
        var cash = report.GetProperty("cash");

        Assert.Single(report.GetProperty("shifts").EnumerateArray());
        Assert.True(cash.GetProperty("isProvisional").GetBoolean());
        Assert.Equal(100m, cash.GetProperty("openingFloat").GetDecimal());
    }

    [Fact]
    public async Task A_days_report_contains_none_of_another_tenants_takings()
    {
        // The tenancy question the by-id theory cannot ask: both shops trade on the same dates,
        // so a leak is a 200 with doubled figures rather than a 404. The report queries are raw
        // SQL with a hand-written tenant predicate, which is exactly where this could go wrong.
        var (mine, tenant) = await factory.TradingTenantAsync();
        var (theirs, other) = await factory.TradingTenantAsync();

        await SellAsync(mine, tenant, (tenant.Catalog.WaterProductId, 2m));
        await SellAsync(theirs, other, (other.Catalog.WaterProductId, 2m));
        await SellAsync(theirs, other, (other.Catalog.CoffeeProductId, 5m));

        var report = await ReportAsync(mine, "/api/v1/reports/daily");

        Assert.Equal(1, report.GetProperty("sales").GetProperty("transactionCount").GetInt32());
        Assert.Single(report.GetProperty("shifts").EnumerateArray());
    }

    [Fact]
    public async Task The_owner_sees_margins_and_a_manager_does_not()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, (tenant.Catalog.CoffeeProductId, 2m));

        var report = await ReportAsync(client, "/api/v1/reports/margins");
        var line = report.GetProperty("lines").EnumerateArray().Single();

        // Coffee: €4.50 each at 23% exclusive, so €9.00 of revenue net of tax against €2.40 of
        // cost apiece.
        Assert.Equal(9.00m, line.GetProperty("revenue").GetDecimal());
        Assert.Equal(4.80m, line.GetProperty("cost").GetDecimal());
        Assert.Equal(4.20m, line.GetProperty("margin").GetDecimal());
        Assert.Equal(4.20m, report.GetProperty("margin").GetDecimal());

        // And the negative half. CanViewMargins is Owner-only because a cost price is the
        // owner's commercial position — a manager who can read it can price-shop the suppliers.
        var manager = await ManagerClientAsync(tenant);

        using var refused = await manager.GetAsync("/api/v1/reports/margins");

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
    }

    [Fact]
    public async Task A_margin_report_contains_none_of_another_tenants_products()
    {
        var (mine, tenant) = await factory.TradingTenantAsync();
        var (theirs, other) = await factory.TradingTenantAsync();

        await SellAsync(mine, tenant, (tenant.Catalog.CoffeeProductId, 1m));
        await SellAsync(theirs, other, (other.Catalog.WaterProductId, 9m));

        var report = await ReportAsync(mine, "/api/v1/reports/margins");
        var line = report.GetProperty("lines").EnumerateArray().Single();

        Assert.Equal(tenant.Catalog.CoffeeProductId, line.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task A_bad_date_is_refused_on_the_field_rather_than_silently_ignored()
    {
        // Not defaulted to today. A report quietly covering a different window than the one
        // asked for is a number somebody will act on.
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.GetAsync("/api/v1/reports/daily?date=last-tuesday");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_shift_report_and_the_day_that_contains_it_agree()
    {
        // The property that makes one ReportScope worth having. Two reports over the same money
        // must produce the same money, whatever route the query took to it.
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, (tenant.Catalog.WaterProductId, 3m));
        await SellAsync(client, tenant, (tenant.Catalog.BagProductId, 4m));

        var shift = await ReportAsync(client, $"/api/v1/shifts/{tenant.ShiftId}/report");
        var day = await ReportAsync(client, "/api/v1/reports/daily");

        Assert.Equal(
            shift.GetProperty("sales").GetProperty("total").GetDecimal(),
            day.GetProperty("sales").GetProperty("total").GetDecimal());

        Assert.Equal(
            shift.GetProperty("sales").GetProperty("tax").GetDecimal(),
            day.GetProperty("sales").GetProperty("tax").GetDecimal());

        Assert.Equal(
            shift.GetProperty("cash").GetProperty("expected").GetDecimal(),
            day.GetProperty("cash").GetProperty("expected").GetDecimal());
    }

    // ---- helpers -------------------------------------------------------------------

    private static decimal Total(JsonElement sale) => sale.GetProperty("total").GetDecimal();

    /// <summary>
    /// Fetches a report, and says what went wrong when it does not come back.
    /// </summary>
    /// <remarks>
    /// The body is in the failure message deliberately. These endpoints are raw SQL, so the
    /// interesting failures are Postgres complaining about a column — and a bare
    /// "expected OK, got InternalServerError" sends the next person to the debugger for
    /// something the response already said.
    /// </remarks>
    private static async Task<JsonElement> ReportAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"GET {url} answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task CashMovementAsync(
        HttpClient client,
        Guid shiftId,
        string type,
        decimal amount,
        string reason)
    {
        using var response = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{shiftId}/cash-movements",
            new { type, amount, reason });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>
    /// Gives the tenant a zone and a day-start offset.
    /// </summary>
    /// <remarks>
    /// Written straight to the row because there is no <c>PUT /settings</c> — these are
    /// onboarding values, and Phase 6 deliberately did not add a route for them.
    /// </remarks>
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
    /// Moves a sale's <c>CompletedAt</c> to a chosen instant.
    /// </summary>
    /// <remarks>
    /// The only way to test a boundary: the endpoint sets <c>CompletedAt</c> from
    /// <c>TimeProvider</c> and no caller may supply it, which is correct — a client that could
    /// would be able to backdate takings. So the row is edited directly, which is a thing a test
    /// may do and the API may not.
    /// </remarks>
    private Task BackdateAsync(Guid tenantId, JsonElement sale, DateTimeOffset completedAt) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var id = sale.GetProperty("id").GetGuid();
            var row = await db.Sales.FirstAsync(s => s.Id == id);

            row.CompletedAt = completedAt;

            await db.SaveChangesAsync();
        });

    /// <summary>Moves a shift's <c>OpenedAt</c>, so an overnight drawer can be tested.</summary>
    private Task BackdateShiftAsync(Guid tenantId, Guid shiftId, DateTimeOffset openedAt) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.FirstAsync(s => s.Id == shiftId);

            shift.OpenedAt = openedAt;

            await db.SaveChangesAsync();
        });

    /// <summary>A Manager in the same tenant, for the negative authorization half.</summary>
    private async Task<HttpClient> ManagerClientAsync(TradingTenant tenant)
    {
        const string email = "manager@trading.test";

        await factory.CreateUserAsync(
            tenant.TenantId, email, TradingTenant.Password, RoleNames.Manager, "Sam Cole");

        var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync(tenant.Slug, email, TradingTenant.Password)).AccessToken);

        return client;
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
                tenders = new[] { new { method = "Cash", amount = 500m } },
            },
            idempotencyKey: clientTransactionId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
