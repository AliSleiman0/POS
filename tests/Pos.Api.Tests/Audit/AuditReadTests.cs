using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Audit;

/// <summary>Reading the log back.</summary>
[Collection(PosApiCollection.Name)]
public sealed class AuditReadTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task Entries_come_back_newest_first_with_the_actors_name()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await AdjustAsync(client, tenant, "First reason");
        await AdjustAsync(client, tenant, "Second reason");

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        var items = page.GetProperty("items").EnumerateArray().ToList();

        Assert.Equal(2, items.Count);

        // Newest first. Also what keeps this endpoint immune to a database that grows for ever
        // — whatever just happened is on page one, whichever run it was.
        Assert.Contains("Second reason", items[0].GetProperty("after").ToString(), StringComparison.Ordinal);
        Assert.Contains("First reason", items[1].GetProperty("after").ToString(), StringComparison.Ordinal);

        // Resolved, not just an id: "who did this" is the question, and a Guid is not an answer.
        Assert.Equal("Ada Byrne", items[0].GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task The_payload_arrives_as_an_object_rather_than_a_string()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await AdjustAsync(client, tenant, "Damaged in transit");

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/audit");
        var after = page.GetProperty("items")[0].GetProperty("after");

        // jsonb both ways. If this came back as a JSON *string*, every consumer would have to
        // parse it a second time and the generated TypeScript client would type it as `string`
        // — which is how a two-column table turns into a blob of escaped quotes on screen.
        Assert.Equal(JsonValueKind.Object, after.ValueKind);
        Assert.Equal("Damaged in transit", after.GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_unknown_action_filter_is_refused_rather_than_ignored()
    {
        var (client, _) = await factory.TradingTenantAsync();

        using var response = await client.GetAsync(
            new Uri("/api/v1/audit?action=NotAnAction", UriKind.Relative));

        // A filter that silently does nothing shows more than was asked for, and the reader
        // concludes they have looked. Same reasoning as GET /sales' type and status filters.
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("action", out _));
    }

    [Fact]
    public async Task Filters_compose()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await AdjustAsync(client, tenant, "Damaged in transit");

        using var sold = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 1m, discountAmount = 0.10m },
            },
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        var filtered = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/audit?action={nameof(AuditAction.StockAdjusted)}&actorId={tenant.OwnerId}");

        var items = filtered.GetProperty("items").EnumerateArray().ToList();

        Assert.Single(items);
        Assert.Equal(nameof(AuditAction.StockAdjusted), items[0].GetProperty("action").GetString());
    }

    [Fact]
    public async Task A_manager_cannot_read_the_audit_log()
    {
        var tenant = await factory.CreateTenantAsync("audit-manager");
        await factory.CreateUserAsync(
            tenant.Id, "manager@audit-manager.test", Password, RoleNames.Manager, "Sam Cole");

        using var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync("audit-manager", "manager@audit-manager.test", Password)).AccessToken);

        // CanManageEmployees is Owner-only. A manager who can read the log can see which of
        // their own actions were recorded, which is the one reader it is least useful to have.
        using var response = await client.GetAsync(new Uri("/api/v1/audit", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_entry_by_a_deactivated_employee_keeps_its_name()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await AdjustAsync(client, tenant, "Before they left");

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var owner = await db.Users.FirstAsync(u => u.Id == tenant.OwnerId);

            owner.IsActive = false;
            await db.SaveChangesAsync();
        });

        var page = await client.GetFromJsonAsync<JsonElement>("/api/v1/audit");

        // An inner join would have dropped the row entirely, hiding exactly the history
        // somebody goes looking for after a member of staff leaves.
        Assert.Equal("Ada Byrne", page.GetProperty("items")[0].GetProperty("actorName").GetString());
    }

    private static async Task AdjustAsync(HttpClient client, TradingTenant tenant, string reason)
    {
        using var response = await client.PostIdempotentAsync("/api/v1/stock/adjustments", new
        {
            productId = tenant.Catalog.WaterProductId,
            type = "Waste",
            quantity = -1m,
            reason,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }
}
