using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Orders;

/// <summary>
/// A manager authorising a cancelled plate at the handheld, without signing in.
/// </summary>
/// <remarks>
/// <b>Why <c>CanVoidFiredLine</c> is on the grant allow-list at all.</b> The list is deliberately
/// short — it is the difference between a scoped approval and a general elevation mechanism — so
/// each addition needs its own argument. This one's is that the alternative is worse for the
/// audit log rather than better: a waiter on a shared handheld cannot hand the device to a
/// supervisor for every cancelled steak, so what actually happens in a real room is a manager
/// session left open all evening, and then every void for the rest of the night is attributed to
/// somebody who was not there.
/// <para>
/// What these tests pin down is that the approval is <b>scoped, single-use, and recorded against
/// the right two people</b>: the actor is whoever was holding the device, the approver is whoever
/// typed the PIN, and they are separate fields.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class FiredLineOverrideTests(PosApiFactory factory)
{
    private const string ManagerPin = "7391";
    private const string ManagerEmail = "manager@restaurant.test";

    [Fact]
    public async Task A_cashier_without_a_grant_cannot_void_a_fired_line()
    {
        var world = await FloorAsync();
        var lineId = await world.FiredLineAsync();

        using var response = await world.Cashier.PostAsJsonAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{lineId}/void",
            new { reason = "Sent back" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A stable slug, not a bare 403: the handheld has to tell "a manager can fix this" apart
        // from every other refusal, or it offers a PIN pad for things no PIN can fix.
        Assert.Contains(
            "override-required",
            problem.GetProperty("type").GetString()!,
            StringComparison.Ordinal);

        await factory.AsTenantAsync(world.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var line = await db.OrderLines.FirstAsync(l => l.Id == lineId);

            Assert.Equal(OrderLineStatus.Fired, line.Status);
        });
    }

    [Fact]
    public async Task A_cashier_with_a_managers_grant_voids_it_and_the_audit_names_both_people()
    {
        var world = await FloorAsync();
        var lineId = await world.FiredLineAsync();

        var grant = await world.MintAsync("CanVoidFiredLine");

        using var response = await world.Cashier.PostWithGrantAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{lineId}/void",
            new { reason = "Sent back — overcooked" },
            grant);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.AsTenantAsync(world.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var line = await db.OrderLines.FirstAsync(l => l.Id == lineId);
            Assert.Equal(OrderLineStatus.Voided, line.Status);

            // The two halves of the point, and the reason the approver is not simply written
            // into ActorId. Who did it is the person holding the device; who allowed it is the
            // manager. A flow that swapped them would make the log name the wrong person on
            // every override in the building.
            var entry = await db.AuditEntries.SingleAsync(e => e.Action == AuditAction.OrderLineVoided);

            Assert.Equal(world.CashierId, entry.ActorId);
            Assert.Contains("approvedBy", entry.After!, StringComparison.Ordinal);
            Assert.Contains(world.ManagerId.ToString(), entry.After!, StringComparison.Ordinal);

            // Spent in the same save as the void it authorised.
            Assert.NotNull((await db.OverrideGrants.SingleAsync()).ConsumedAt);

            // And it was not attributed to a sale, because no sale happened — a cancelled plate
            // is a loss, not a transaction. See OverrideGrantService.Consume(grant).
            Assert.Null((await db.OverrideGrants.SingleAsync()).ConsumedBySaleId);
        });
    }

    [Fact]
    public async Task A_grant_is_spent_once_and_will_not_void_a_second_line()
    {
        var world = await FloorAsync();
        var first = await world.FiredLineAsync();
        var second = await world.FiredLineAsync();

        var grant = await world.MintAsync("CanVoidFiredLine");

        using var voided = await world.Cashier.PostWithGrantAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{first}/void",
            new { reason = "Overcooked" },
            grant);

        Assert.Equal(HttpStatusCode.OK, voided.StatusCode);

        // Single use is the security boundary, not the five-minute lifetime — see OverrideGrant.
        // Without this, one PIN at the start of service would clear plates all night.
        using var replayed = await world.Cashier.PostWithGrantAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{second}/void",
            new { reason = "And this one" },
            grant);

        Assert.Equal(HttpStatusCode.Forbidden, replayed.StatusCode);
    }

    [Fact]
    public async Task A_grant_for_a_different_policy_does_not_open_this_door()
    {
        var world = await FloorAsync();
        var lineId = await world.FiredLineAsync();

        // The allow-list grew; it did not stop being a list. A discount grant authorises a
        // discount and nothing else, which is what keeps "scoped" true rather than approximate.
        var grant = await world.MintAsync("CanApplyDiscount");

        using var response = await world.Cashier.PostWithGrantAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{lineId}/void",
            new { reason = "Trying it on" },
            grant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_supervisor_on_the_floor_needs_no_grant_and_spends_none()
    {
        var world = await FloorAsync();
        var lineId = await world.FiredLineAsync();

        // The Owner holds the policy themselves, so nothing is presented and nothing is spent —
        // a manager working the floor never authorises anything to themselves.
        using var response = await world.Owner.PostAsJsonAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{lineId}/void",
            new { reason = "Dropped it" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.AsTenantAsync(world.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.OverrideGrants.ToListAsync());
        });
    }

    [Fact]
    public async Task A_pending_line_still_needs_neither_a_grant_nor_a_reason()
    {
        var world = await FloorAsync();

        var order = await world.Cashier.AddLineAsync(world.OrderId, world.WaterProductId);
        var pending = order.GetProperty("lines").EnumerateArray().Last().GetProperty("id").GetGuid();

        // The distinction the whole status exists for: nobody cooked this, so it costs the shop
        // nothing and is not worth a manager's time or a line in the audit log.
        using var response = await world.Cashier.PostAsJsonAsync(
            $"/api/v1/orders/{world.OrderId}/lines/{pending}/void",
            new { reason = (string?)null });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.AsTenantAsync(world.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.AuditEntries.Where(e => e.Action == AuditAction.OrderLineVoided).ToListAsync());
        });
    }

    /// <summary>A restaurant with an enrolled till, a cashier, a manager with a PIN, and a table seated.</summary>
    private async Task<Floor> FloorAsync()
    {
        var (owner, tenant) = await factory.RestaurantTenantAsync();

        var deviceToken = await factory.EnrolRegisterAsync(tenant.TenantId, tenant.RegisterId);

        var manager = await factory.CreateUserAsync(
            tenant.TenantId, ManagerEmail, RestaurantTenant.Password, RoleNames.Manager, "Sam Cole");

        await factory.SetPinAsync(tenant.TenantId, manager.Id, ManagerPin);

        var cashier = await factory.CashierClientAsync(tenant);

        // A station and a routed category, or nothing can be fired and there is no fired line to
        // argue about.
        var pass = await owner.CreateStationAsync("Pass");
        await owner.RouteCategoryAsync(
            tenant.Catalog.GroceryCategoryId, CatalogFixture.GroceryCategoryName, pass);

        var orderId = await owner.SeatAsync(tenant.TableId);

        return new Floor(
            tenant.TenantId,
            orderId,
            tenant.Catalog.WaterProductId,
            deviceToken,
            owner,
            cashier,
            tenant.CashierId,
            manager.Id,
            factory);
    }

    private sealed record Floor(
        Guid TenantId,
        Guid OrderId,
        Guid WaterProductId,
        string DeviceToken,
        HttpClient Owner,
        HttpClient Cashier,
        Guid CashierId,
        Guid ManagerId,
        PosApiFactory Factory)
    {
        /// <summary>Puts one item on the order and sends it, returning the fired line's id.</summary>
        public async Task<Guid> FiredLineAsync()
        {
            var order = await Owner.AddLineAsync(OrderId, WaterProductId);
            var lineId = order.GetProperty("lines").EnumerateArray().Last().GetProperty("id").GetGuid();

            using var fired = await Owner.PostIdempotentAsync(
                $"/api/v1/orders/{OrderId}/fire",
                new { course = (int?)null });

            Assert.Equal(HttpStatusCode.OK, fired.StatusCode);

            return lineId;
        }

        /// <summary>Mints a grant the way a handheld does: manager PIN, from the enrolled till.</summary>
        public async Task<string> MintAsync(string policy)
        {
            using var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add(
                DeviceTokenAuthenticationHandler.HeaderName,
                DeviceToken);

            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/override",
                new { userId = ManagerId, pin = ManagerPin, policies = new[] { policy } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("grant").GetString()!;
        }
    }
}
