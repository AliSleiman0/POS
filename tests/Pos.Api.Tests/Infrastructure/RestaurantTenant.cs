using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>
/// A restaurant of its own: a room with two tables, a catalog, a register and an open shift.
/// </summary>
/// <remarks>
/// One per test, for the reason <see cref="TradingTenant"/> is: these tests assert exact counts —
/// how many orders a table ended up with, how many lines a merge moved — and only a tenant nobody
/// else is trading in can express that. There is also a hard constraint that makes sharing
/// impossible: at most one order may be open per table, so a shared room would have every test
/// after the first fighting the one before it.
/// <para>
/// It carries a shift and a register because a bill is settled as an ordinary sale, and a sale
/// needs an open drawer. Nothing in 10.1–10.4 uses them; 10.5 does.
/// </para>
/// </remarks>
public sealed record RestaurantTenant(
    Guid TenantId,
    string Slug,
    Guid OwnerId,
    Guid CashierId,
    Guid RegisterId,
    Guid ShiftId,
    Guid AreaId,
    Guid TableId,
    Guid SecondTableId,
    SeededCatalog Catalog)
{
    public const string OwnerEmail = "owner@restaurant.test";
    public const string CashierEmail = "robin@restaurant.test";
    public const string Password = "Correct-Horse-9";
}

public static class RestaurantTenantExtensions
{
    /// <summary>
    /// Creates a restaurant-mode tenant with a room, and returns a client signed in as its Owner.
    /// </summary>
    /// <remarks>
    /// The Owner, because these tests mostly arrange things a Cashier may not — building the
    /// floor, abandoning an order. Where the <i>permission</i> is the subject, the test signs a
    /// Cashier in separately with <see cref="CashierClientAsync"/>.
    /// </remarks>
    public static async Task<(HttpClient Client, RestaurantTenant Tenant)> RestaurantTenantAsync(
        this PosApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var slug = $"rest-{Guid.CreateVersion7():N}"[..24];

        var tenant = await factory.CreateTenantAsync(slug, "The Corner Table", ServiceMode.Restaurant);

        var owner = await factory.CreateUserAsync(
            tenant.Id, RestaurantTenant.OwnerEmail, RestaurantTenant.Password, RoleNames.Owner, "Ada Byrne");

        var cashier = await factory.CreateUserAsync(
            tenant.Id, RestaurantTenant.CashierEmail, RestaurantTenant.Password, RoleNames.Cashier, "Robin Vale");

        var catalog = await CatalogFixture.WriteAsync(factory, tenant.Id);

        Guid registerId = default;
        Guid areaId = default;
        Guid tableId = default;
        Guid secondTableId = default;

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Inclusive, which is what a European restaurant quotes on a menu — the price on
            // the card is the price paid, and a till that added tax at the end would be wrong
            // about every dish.
            var row = await db.Tenants.FindAsync(tenant.Id);
            row!.TaxMode = TaxMode.Inclusive;

            var register = new Register { Name = "Bar Till", IsActive = true };
            db.Registers.Add(register);

            // The room is written directly rather than through POST /floor/areas, so a test of
            // the floor endpoints is not arranged by the endpoints it is testing.
            var area = new ServiceArea { Name = "Main room", SortOrder = 1 };
            db.ServiceAreas.Add(area);

            await db.SaveChangesAsync();

            var first = new DiningTable { ServiceAreaId = area.Id, Name = "4", Seats = 4, SortOrder = 1 };
            var second = new DiningTable { ServiceAreaId = area.Id, Name = "5", Seats = 2, SortOrder = 2 };

            db.DiningTables.AddRange(first, second);
            await db.SaveChangesAsync();

            registerId = register.Id;
            areaId = area.Id;
            tableId = first.Id;
            secondTableId = second.Id;
        });

        var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync(slug, RestaurantTenant.OwnerEmail, RestaurantTenant.Password)).AccessToken);

        var shiftId = await client.OpenShiftAsync(registerId);

        return (
            client,
            new RestaurantTenant(
                tenant.Id,
                slug,
                owner.Id,
                cashier.Id,
                registerId,
                shiftId,
                areaId,
                tableId,
                secondTableId,
                catalog));
    }

    /// <summary>A second client, signed in as the shop's Cashier.</summary>
    public static async Task<HttpClient> CashierClientAsync(
        this PosApiFactory factory,
        RestaurantTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(tenant);

        var client = factory.CreateClient();

        client.WithBearer(
            (await client.LoginAsync(
                tenant.Slug,
                RestaurantTenant.CashierEmail,
                RestaurantTenant.Password)).AccessToken);

        return client;
    }

    /// <summary>Seats a table through the real endpoint, so the tests exercise it too.</summary>
    public static async Task<Guid> SeatAsync(this HttpClient client, Guid tableId, int covers = 2)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.PostIdempotentAsync(
            "/api/v1/orders",
            new { type = nameof(OrderType.Table), diningTableId = tableId, coverCount = covers });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    /// <summary>Puts one item on an order and returns the whole order as the API projects it.</summary>
    public static async Task<JsonElement> AddLineAsync(
        this HttpClient client,
        Guid orderId,
        Guid productId,
        decimal quantity = 1m,
        int course = 1,
        int? seat = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        using var response = await client.PostIdempotentAsync(
            $"/api/v1/orders/{orderId}/lines",
            new { lines = new[] { new { productId, quantity, course, seatNumber = seat } } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }
}
