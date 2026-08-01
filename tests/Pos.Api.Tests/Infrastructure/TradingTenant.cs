using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>
/// A tenant of its own, with a catalog, a register and an open shift: everything a sale needs.
/// </summary>
/// <remarks>
/// <b>One per test, deliberately.</b> The sale and shift tests assert exact counts — how many
/// movements a sale wrote, how many sales twenty parallel submissions produced — and those are
/// only meaningful in a tenant nothing else is trading in. The shared sandbox's "assert
/// contains, never count" rule cannot express them.
/// <para>
/// It is also the only way to test shifts honestly: at most one shift may be open per
/// register, so a suite sharing a register would have every test after the first fighting the
/// one before it.
/// </para>
/// </remarks>
public sealed record TradingTenant(
    Guid TenantId,
    string Slug,
    Guid OwnerId,
    Guid RegisterId,
    Guid ShiftId,
    SeededCatalog Catalog)
{
    public const string OwnerEmail = "owner@trading.test";
    public const string Password = "Correct-Horse-9";
}

public static class TradingTenantExtensions
{
    /// <summary>
    /// Creates a fresh tenant with a catalog, an active register and an open shift, and
    /// returns a client signed in as its Owner.
    /// </summary>
    /// <remarks>
    /// The Owner rather than a Cashier because these tests mostly need to arrange things a
    /// Cashier may not — creating registers, reading margins, adjusting stock. Where the
    /// <i>permission</i> is the subject, the test signs a Cashier in separately.
    /// </remarks>
    public static async Task<(HttpClient Client, TradingTenant Tenant)> TradingTenantAsync(
        this PosApiFactory factory,
        decimal cashRoundingIncrement = 0m)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var slug = $"trade-{Guid.CreateVersion7():N}"[..24];

        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");
        var owner = await factory.CreateUserAsync(
            tenant.Id, TradingTenant.OwnerEmail, TradingTenant.Password, RoleNames.Owner, "Ada Byrne");

        var catalog = await CatalogFixture.WriteAsync(factory, tenant.Id);

        Guid registerId = default;

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Set here rather than through an endpoint because /settings is not built: TaxMode
            // and the cash-rounding rule are onboarding values with no route yet, and Phase 3
            // deliberately did not add one.
            var row = await db.Tenants.FindAsync(tenant.Id);
            row!.TaxMode = TaxMode.Exclusive;
            row.CashRoundingIncrement = cashRoundingIncrement;

            var register = new Register { Name = "Front Counter", IsActive = true };
            db.Registers.Add(register);

            await db.SaveChangesAsync();

            registerId = register.Id;
        });

        var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync(slug, TradingTenant.OwnerEmail, TradingTenant.Password)).AccessToken);

        var shiftId = await OpenShiftAsync(client, registerId);

        return (client, new TradingTenant(tenant.Id, slug, owner.Id, registerId, shiftId, catalog));
    }

    /// <summary>Opens a shift through the real endpoint, so the tests exercise it too.</summary>
    public static async Task<Guid> OpenShiftAsync(this HttpClient client, Guid registerId, decimal openingFloat = 100m)
    {
        using var response = await client.PostIdempotentAsync(
            "/api/v1/shifts",
            new { registerId, openingFloat });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
