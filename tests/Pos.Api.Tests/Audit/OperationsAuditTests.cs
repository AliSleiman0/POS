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

/// <summary>Stock adjustments, drawer closes, tills and staff.</summary>
[Collection(PosApiCollection.Name)]
public sealed class StockAuditTests(PosApiFactory factory)
{
    [Fact]
    public async Task An_adjustment_records_the_reason_and_the_resulting_count()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var adjusted = await client.PostIdempotentAsync("/api/v1/stock/adjustments", new
        {
            productId = tenant.Catalog.WaterProductId,
            type = "Waste",
            quantity = -3m,
            reason = "Damaged in transit",
        });

        Assert.Equal(HttpStatusCode.Created, adjusted.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.StockAdjusted);

            // Stock leaving a shop by hand is the thing this entry exists to make visible. The
            // movement row already says what changed; this says who decided it should, and
            // carries the resulting count so a reader does not have to replay the ledger.
            Assert.Equal(nameof(Product), entry.EntityType);
            Assert.Equal(tenant.Catalog.WaterProductId, entry.EntityId);
            Assert.Contains("Damaged in transit", entry.After!, StringComparison.Ordinal);
            Assert.Contains("Waste", entry.After!, StringComparison.Ordinal);
            Assert.Contains("9", entry.After!, StringComparison.Ordinal);
            Assert.NotNull(entry.ActorId);
        });
    }

    [Fact]
    public async Task A_sale_moving_stock_writes_no_adjustment_entry()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var sold = await client.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // A sale moves stock too, and auditing that would file an entry for every item in
            // every basket. `StockAdjusted` means somebody moved stock *by hand*, which is the
            // only version of the event worth reading.
            Assert.False(await db.AuditEntries.AnyAsync(a => a.Action == AuditAction.StockAdjusted));
        });
    }
}

[Collection(PosApiCollection.Name)]
public sealed class ShiftAuditTests(PosApiFactory factory)
{
    [Fact]
    public async Task Closing_a_drawer_records_the_variance()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var closed = await client.PostIdempotentAsync(
            $"/api/v1/shifts/{tenant.ShiftId}/close",
            new { countedCash = 90m });

        Assert.Equal(HttpStatusCode.OK, closed.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.ShiftClosed);

            Assert.Equal(tenant.ShiftId, entry.EntityId);

            // All three numbers, so a reader can see the variance *and* check it, without
            // having to trust that the stored figure was computed from the other two.
            Assert.Contains("countedCash", entry.After!, StringComparison.Ordinal);
            Assert.Contains("expectedCash", entry.After!, StringComparison.Ordinal);
            Assert.Contains("variance", entry.After!, StringComparison.Ordinal);
        });
    }
}

[Collection(PosApiCollection.Name)]
public sealed class RegisterAuditTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task Enrolling_a_till_is_audited_without_recording_the_token()
    {
        var (client, tenant) = await OwnerOfAsync("audit-enroll");

        using var created = await client.PostAsJsonAsync("/api/v1/registers", new { name = "Front Till" });
        var registerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        using var enrolled = await client.PostAsJsonAsync($"/api/v1/registers/{registerId}/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);

        var token = (await enrolled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deviceToken").GetString()!;

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.DeviceEnrolled);

            Assert.Equal(registerId, entry.EntityId);
            Assert.Contains("Front Till", entry.After!, StringComparison.Ordinal);

            // An audit trail that stores credentials is a credential store with worse access
            // control — and this one is readable by anybody who can manage staff.
            Assert.DoesNotContain(token, entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Revoking_a_till_is_audited()
    {
        var (client, tenant) = await OwnerOfAsync("audit-revoke");

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id, "Back Till");

        using var revoked = await client.PostAsJsonAsync($"/api/v1/registers/{till.Id}/revoke", new { });
        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries.SingleAsync(a => a.Action == AuditAction.DeviceRevoked);

            Assert.Equal(till.Id, entry.EntityId);
        });
    }

    private async Task<(HttpClient Client, Tenant Tenant)> OwnerOfAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug);
        await factory.CreateUserAsync(tenant.Id, $"owner@{slug}.test", Password, RoleNames.Owner, "Pat Keeper");

        var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(slug, $"owner@{slug}.test", Password)).AccessToken);

        return (client, tenant);
    }
}

[Collection(PosApiCollection.Name)]
public sealed class EmployeeAuditTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Deactivating_somebody_is_audited_by_either_route(bool viaEditForm)
    {
        var slug = viaEditForm ? "audit-deact-put" : "audit-deact-post";

        var tenant = await factory.CreateTenantAsync(slug);
        await factory.CreateUserAsync(tenant.Id, $"owner@{slug}.test", Password, RoleNames.Owner, "Pat Keeper");

        var staff = await factory.CreateUserAsync(
            tenant.Id, $"leaver@{slug}.test", Password, RoleNames.Cashier, "Leaver");

        using var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(slug, $"owner@{slug}.test", Password)).AccessToken);

        // Both doors, because there are two: the dedicated route and the edit form's "can sign
        // in" checkbox. One of them writing no entry would leave a way to remove somebody's
        // access with nothing recorded, which is exactly the gap an audit log exists to close.
        using var response = viaEditForm
            ? await client.PutAsJsonAsync(
                $"/api/v1/employees/{staff.Id}",
                new { displayName = "Leaver", role = RoleNames.Cashier, isActive = false })
            : await client.PostAsJsonAsync($"/api/v1/employees/{staff.Id}/deactivate", new { });

        Assert.True(response.IsSuccessStatusCode);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.EmployeeDeactivated && a.EntityId == staff.Id);

            Assert.Equal(nameof(ApplicationUser), entry.EntityType);
            Assert.Contains("True", entry.Before!, StringComparison.Ordinal);
            Assert.Contains("False", entry.After!, StringComparison.Ordinal);
        });
    }
}
