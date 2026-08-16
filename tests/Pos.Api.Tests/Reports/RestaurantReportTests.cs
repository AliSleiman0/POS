using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Orders;

namespace Pos.Api.Tests.Reports;

/// <summary>
/// Takings by table and by server, and the two claims that make them trustworthy.
/// </summary>
/// <remarks>
/// <b>The figures come off the <c>sale</c> row, joined through <c>OrderBill.SaleId</c>.</b> Never
/// from the catalog — invariant 5 — because pricing an old table from today's menu would
/// retroactively rewrite what the shop took and stop the report reconciling with the cash.
/// <para>
/// <b>And a shift's report and the day's report that contains it still add up to the same
/// money.</b> That is the exit criterion these sections had to not break: one
/// <c>ReportScope</c> feeds one set of aggregations and only the window differs, so a second
/// shape with a second set of predicates is exactly how it would stop being true.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class RestaurantReportTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_retail_shop_gets_no_restaurant_sections_at_all()
    {
        var (client, _) = await factory.TradingTenantAsync();

        var report = await DailyAsync(client);

        // Empty rather than absent. A counter has no tables, and a nullable section would make
        // every reader branch on a distinction already visible in the data.
        Assert.Equal(0, report.GetProperty("byTable").GetArrayLength());
        Assert.Equal(0, report.GetProperty("byServer").GetArrayLength());
    }

    [Fact]
    public async Task A_settled_table_appears_against_its_table_and_its_server_with_the_tip_apart()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId, covers: 2);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, quantity: 2m);

        var (billId, total) = await BillAsync(client, orderId);

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{billId}/pay",
            new
            {
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                tenders = new[] { new { amount = total + 10m } },
                tip = 3m,
            });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var report = await DailyAsync(client);

        var table = Assert.Single(report.GetProperty("byTable").EnumerateArray());

        Assert.Equal("4", table.GetProperty("tableName").GetString());
        Assert.Equal(2, table.GetProperty("covers").GetInt32());
        Assert.Equal(1, table.GetProperty("orders").GetInt32());

        // The tip is beside the takings, never inside them. Folding it in would inflate revenue
        // and inflate the tax owed on revenue nobody was charged tax for.
        Assert.Equal(total, table.GetProperty("total").GetDecimal());
        Assert.Equal(3m, table.GetProperty("tips").GetDecimal());

        // Spend per head, which is the number a restaurant owner manages by.
        Assert.Equal(total / 2m, table.GetProperty("averageSpendPerCover").GetDecimal());

        var server = Assert.Single(report.GetProperty("byServer").EnumerateArray());

        // Whoever opened the order, not whoever rang the sale. A tips-by-server report keyed on
        // the till would hand the evening's gratuities to whoever stood at it.
        Assert.Equal("Ada Byrne", server.GetProperty("serverName").GetString());
        Assert.Equal(3m, server.GetProperty("tips").GetDecimal());
    }

    [Fact]
    public async Task A_table_with_no_cover_count_reports_no_average_rather_than_a_wrong_one()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        // A takeaway legitimately has no covers, and neither does a table nobody keyed one at.
        using var opened = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = "Takeaway" });

        var orderId = (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId);

        var (billId, total) = await BillAsync(client, orderId);

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{billId}/pay",
            new
            {
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                tenders = new[] { new { amount = total } },
            });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var report = await DailyAsync(client);
        var row = Assert.Single(report.GetProperty("byTable").EnumerateArray());

        // Null, not the takings. Dividing by a missing denominator and calling it an average
        // would put a plausible, wrong number on a report somebody makes decisions with.
        Assert.Equal(JsonValueKind.Null, row.GetProperty("averageSpendPerCover").ValueKind);
    }

    [Fact]
    public async Task The_shifts_report_and_the_days_report_still_agree_about_the_money()
    {
        var (client, tenant) = await factory.RestaurantTenantAsync();

        var orderId = await client.SeatAsync(tenant.TableId, covers: 2);
        await client.AddLineAsync(orderId, tenant.Catalog.WaterProductId, quantity: 3m);

        var (billId, total) = await BillAsync(client, orderId);

        using var paid = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills/{billId}/pay",
            new
            {
                registerId = tenant.RegisterId,
                shiftId = tenant.ShiftId,
                // The tender has to cover the bill AND the tip: the tip comes out of the
                // change, so a guest leaving one hands over more, not the same.
                tenders = new[] { new { amount = total + 2m } },
                tip = 2m,
            });

        Assert.Equal(HttpStatusCode.OK, paid.StatusCode);

        var day = await DailyAsync(client);

        using var shiftResponse = await client.GetAsync(
            new Uri($"/api/v1/shifts/{tenant.ShiftId}/report", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, shiftResponse.StatusCode);

        var shift = await shiftResponse.Content.ReadFromJsonAsync<JsonElement>();

        // The exit criterion, and the reason both reports are one shape over one set of
        // aggregations: only the window differs. Two shapes is how they drift.
        Assert.Equal(
            day.GetProperty("sales").GetProperty("total").GetDecimal(),
            shift.GetProperty("sales").GetProperty("total").GetDecimal());

        Assert.Equal(
            day.GetProperty("byTable")[0].GetProperty("total").GetDecimal(),
            shift.GetProperty("byTable")[0].GetProperty("total").GetDecimal());

        Assert.Equal(
            day.GetProperty("byTable")[0].GetProperty("tips").GetDecimal(),
            shift.GetProperty("byTable")[0].GetProperty("tips").GetDecimal());
    }

    private static async Task<JsonElement> DailyAsync(HttpClient client)
    {
        using var response = await client.GetAsync(new Uri("/api/v1/reports/daily", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<(Guid BillId, decimal Total)> BillAsync(HttpClient client, Guid orderId)
    {
        using var response = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/bills",
            new { allocations = (object?)null });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var bill = await response.Content.ReadFromJsonAsync<JsonElement>();

        return (bill.GetProperty("id").GetGuid(), bill.GetProperty("total").GetDecimal());
    }
}
