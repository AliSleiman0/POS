using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// A cashier discounting a line on a manager's authority.
/// </summary>
/// <remarks>
/// The behaviour these pin down is the whole reason <c>override_grant</c> exists rather than a
/// short-lived token: <b>one PIN authorises one sale.</b> A grant that merely expired after a
/// few minutes would let a single manager PIN discount every sale in that window, which is the
/// fraud the flow is supposed to prevent.
/// <para>
/// The other half is attribution. The sale stays the cashier's — <c>POST /sales</c> takes that
/// from the token and always has — while <c>SaleLine.OverriddenBy</c> names the manager who
/// approved the exception.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleOverrideTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/sales";
    private const string QuoteRoute = "/api/v1/sales/quote";
    private const string ManagerPin = "7391";

    [Fact]
    public async Task A_cashier_with_a_managers_grant_may_override_a_price_and_the_line_names_the_manager()
    {
        var world = await TillAsync();

        var grant = await world.MintAsync("CanOverridePrice");

        using var response = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: grant);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var saleId = body.GetProperty("id").GetGuid();

        Assert.True(body.GetProperty("lines")[0].GetProperty("isPriceOverridden").GetBoolean());

        await factory.AsTenantAsync(world.Tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var sale = await db.Sales.SingleAsync(s => s.Id == saleId);
            var line = await db.SaleLines.SingleAsync(l => l.SaleId == saleId);

            // The two halves of the point. The takings belong to whoever was on the till; the
            // exception belongs to whoever approved it. A flow that swapped the session would
            // have written the manager into both and made the Z-report reconcile the wrong
            // person's drawer.
            Assert.Equal(world.CashierId, sale.CashierId);
            Assert.Equal(world.ManagerId, line.OverriddenBy);
        });
    }

    [Fact]
    public async Task A_grant_is_spent_once()
    {
        // The single-use rule, and the reason this is a table rather than a signed token with a
        // short expiry. Falsify by removing the ConsumedAt write in OverrideGrantService.Consume:
        // the second sale then succeeds and one manager PIN discounts an afternoon.
        var world = await TillAsync();

        var grant = await world.MintAsync("CanOverridePrice");

        using var first = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: grant);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        // A different sale — a new client transaction id and a new idempotency key — so this is
        // genuinely a second piece of work rather than a replay of the first.
        using var second = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: grant);

        Assert.Equal(HttpStatusCode.Forbidden, second.StatusCode);
        await AssertOverrideRequiredAsync(second);

        await factory.AsTenantAsync(world.Tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // One sale, and the grant records which one spent it.
            var sale = await db.Sales.SingleAsync();
            var row = await db.OverrideGrants.SingleAsync();

            Assert.NotNull(row.ConsumedAt);
            Assert.Equal(sale.Id, row.ConsumedBySaleId);
        });
    }

    [Fact]
    public async Task A_grant_minted_at_another_till_is_refused()
    {
        // A manager approves something at the counter they are standing at. Carrying the grant
        // to the till in the back room would mean approving a sale they never saw.
        var world = await TillAsync();

        var otherTill = await factory.CreateEnrolledRegisterAsync(world.Tenant.TenantId, "Back Room");
        var elsewhere = await world.MintAsync("CanOverridePrice", otherTill.DeviceToken);

        using var response = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: elsewhere);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        await AssertOverrideRequiredAsync(response);
    }

    [Fact]
    public async Task An_expired_grant_is_refused()
    {
        var world = await TillAsync();

        var grant = await world.MintAsync("CanOverridePrice");

        // Aged in the database rather than by waiting five minutes. The clock is the subject
        // here, not the mechanism, and a test that slept would be five minutes of CI per run.
        await factory.AsTenantAsync(world.Tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var row = await db.OverrideGrants.SingleAsync();

            row.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);

            await db.SaveChangesAsync();
        });

        using var response = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: grant);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_grant_from_another_tenant_authorises_nothing()
    {
        // The tenant query filter, with RLS underneath it — not a check anybody wrote in the
        // resolver. Shop B's manager cannot approve a discount in shop A however valid their
        // PIN, their till and their token are at home.
        var world = await TillAsync();

        var (_, otherTenant) = await factory.TradingTenantAsync();
        var otherTill = await factory.EnrolRegisterAsync(otherTenant.TenantId, otherTenant.RegisterId);

        var otherManager = await factory.CreateUserAsync(
            otherTenant.TenantId, "manager@other.test", TradingTenant.Password, RoleNames.Manager, "Other Manager");

        await factory.SetPinAsync(otherTenant.TenantId, otherManager.Id, ManagerPin);

        using var otherClient = factory.CreateClient();
        otherClient.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, otherTill);

        using var minted = await otherClient.PostAsJsonAsync(
            "/api/v1/auth/override",
            new { userId = otherManager.Id, pin = ManagerPin, policies = new[] { "CanOverridePrice" } });

        Assert.Equal(HttpStatusCode.OK, minted.StatusCode);

        var foreign = (await minted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("grant").GetString()!;

        using var response = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: foreign);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_grant_authorises_only_what_it_was_minted_for()
    {
        // Minted for a discount, presented for a price override. A grant that covered whatever
        // it was shown to would make the policy names decoration.
        var world = await TillAsync();

        var discountOnly = await world.MintAsync("CanApplyDiscount");

        using var response = await world.Cashier.PostIdempotentAsync(Route, OverriddenSale(world), overrideGrant: discountOnly);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_quote_needs_the_same_authorisation_the_sale_will()
    {
        // Otherwise the till shows a discounted total the sale then refuses: the cashier reads
        // it out, the customer counts out the money, and only then does it come back 403.
        var world = await TillAsync();

        using var refused = await world.Cashier.PostAsJsonAsync(QuoteRoute, DiscountedCart(world));

        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        await AssertOverrideRequiredAsync(refused);

        var grant = await world.MintAsync("CanApplyDiscount");

        using var priced = await world.Cashier.PostWithGrantAsync(QuoteRoute, DiscountedCart(world), grant);

        Assert.Equal(HttpStatusCode.OK, priced.StatusCode);

        var body = await priced.Content.ReadFromJsonAsync<JsonElement>();

        // Water is 1.2000 at 23%: one unit is 1.48 payable, less 0.50 off the cart.
        Assert.Equal(0.50m, body.GetProperty("discountTotal").GetDecimal());
        Assert.Equal(0.86m, body.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_quote_does_not_spend_the_grant()
    {
        // The register re-quotes on every keystroke. Spending a single-use grant on the first
        // of those would leave nothing for the sale it was minted for — the discount would
        // appear on screen and then be refused at the moment money changed hands.
        var world = await TillAsync();

        var grant = await world.MintAsync("CanApplyDiscount");

        for (var quote = 0; quote < 3; quote++)
        {
            using var priced = await world.Cashier.PostWithGrantAsync(QuoteRoute, DiscountedCart(world), grant);

            Assert.Equal(HttpStatusCode.OK, priced.StatusCode);
        }

        using var sale = await world.Cashier.PostIdempotentAsync(Route, DiscountedSale(world), overrideGrant: grant);

        Assert.Equal(HttpStatusCode.Created, sale.StatusCode);

        await factory.AsTenantAsync(world.Tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.NotNull((await db.OverrideGrants.SingleAsync()).ConsumedAt);
        });
    }

    [Fact]
    public async Task A_manager_on_the_till_needs_no_grant_at_all()
    {
        // The positive control for the caller's own policy. Without it every test above would
        // pass against an endpoint that had simply stopped accepting discounts.
        var world = await TillAsync();

        using var response = await world.Manager.PostIdempotentAsync(Route, DiscountedSale(world));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        await factory.AsTenantAsync(world.Tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Nothing was minted: a manager standing at the till does not authorise themselves.
            Assert.Empty(await db.OverrideGrants.ToListAsync());
        });
    }

    private static object OverriddenSale(Till world) => new
    {
        clientTransactionId = Guid.CreateVersion7(),
        registerId = world.Tenant.RegisterId,
        shiftId = world.Tenant.ShiftId,
        lines = new[]
        {
            new { productId = world.Tenant.Catalog.WaterProductId, quantity = 1m, unitPriceOverride = 0.50m },
        },
        tenders = new[] { new { method = "Cash", amount = 5m } },
    };

    private static object DiscountedSale(Till world) => new
    {
        clientTransactionId = Guid.CreateVersion7(),
        registerId = world.Tenant.RegisterId,
        shiftId = world.Tenant.ShiftId,
        lines = new[] { new { productId = world.Tenant.Catalog.WaterProductId, quantity = 1m } },
        cartDiscountAmount = 0.50m,
        tenders = new[] { new { method = "Cash", amount = 5m } },
    };

    private static object DiscountedCart(Till world) => new
    {
        lines = new[] { new { productId = world.Tenant.Catalog.WaterProductId, quantity = 1m } },
        cartDiscountAmount = 0.50m,
    };

    private static async Task AssertOverrideRequiredAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        // A stable slug, not a bare 403: the till has to tell "a manager can fix this" apart
        // from every other refusal, or it offers a PIN pad for things no PIN can fix.
        Assert.Contains(
            "override-required",
            body.GetProperty("type").GetString()!,
            StringComparison.Ordinal);
    }

    /// <summary>A trading tenant whose till is enrolled, with a cashier and a manager on it.</summary>
    private sealed record Till(
        TradingTenant Tenant,
        string DeviceToken,
        HttpClient Cashier,
        Guid CashierId,
        HttpClient Manager,
        Guid ManagerId,
        PosApiFactory Factory)
    {
        /// <summary>Mints a grant the way the register does: manager PIN, from the till.</summary>
        public async Task<string> MintAsync(string policy, string? deviceToken = null)
        {
            using var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Add(
                DeviceTokenAuthenticationHandler.HeaderName,
                deviceToken ?? DeviceToken);

            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/override",
                new { userId = ManagerId, pin = ManagerPin, policies = new[] { policy } });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("grant").GetString()!;
        }
    }

    private async Task<Till> TillAsync()
    {
        var (_, tenant) = await factory.TradingTenantAsync();

        var deviceToken = await factory.EnrolRegisterAsync(tenant.TenantId, tenant.RegisterId);

        var cashier = await factory.CreateUserAsync(
            tenant.TenantId, "cashier@trading.test", TradingTenant.Password, RoleNames.Cashier, "Robin Vale");

        var manager = await factory.CreateUserAsync(
            tenant.TenantId, "manager@trading.test", TradingTenant.Password, RoleNames.Manager, "Sam Cole");

        await factory.SetPinAsync(tenant.TenantId, manager.Id, ManagerPin);

        var cashierClient = factory.CreateClient();
        cashierClient.WithBearer((await cashierClient.LoginAsync(
            tenant.Slug, "cashier@trading.test", TradingTenant.Password)).AccessToken);

        var managerClient = factory.CreateClient();
        managerClient.WithBearer((await managerClient.LoginAsync(
            tenant.Slug, "manager@trading.test", TradingTenant.Password)).AccessToken);

        return new Till(tenant, deviceToken, cashierClient, cashier.Id, managerClient, manager.Id, factory);
    }
}
