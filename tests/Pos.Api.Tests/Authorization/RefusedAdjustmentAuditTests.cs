using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Authorization;

/// <summary>
/// A cashier reaching for a discount they do not hold, and the record it leaves.
/// </summary>
/// <remarks>
/// §7.3's fifth bullet, and the one part of the phase's authorization work that cannot be
/// derived from the policy map: the refusal itself is ordinary, but <b>a refused attempt is
/// exactly what an owner wants to see</b> and it leaves no other trace anywhere. A cashier who
/// tries a discount on every third basket and is refused every time looks, from every other
/// table in the database, like a cashier who never tried.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class RefusedAdjustmentAuditTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_cashiers_discount_is_refused_and_the_attempt_is_recorded()
    {
        var (tenant, cashier, cashierId) = await TillAsync("refused-discount");

        var clientTransactionId = Guid.CreateVersion7();

        using var response = await cashier.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId,
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            cartDiscountAmount = 0.50m,
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("override-required", problem.GetProperty("type").GetString()!, StringComparison.Ordinal);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.AuthorizationRefused);

            // The cashier, not the manager who was never asked. And keyed on the client
            // transaction id, because there is no sale — that id is the only handle the
            // attempt ever had, and it is what the till would retry with.
            Assert.Equal(cashierId, entry.ActorId);
            Assert.Equal(clientTransactionId, entry.EntityId);
            Assert.Contains("CanApplyDiscount", entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_cashiers_price_override_is_refused_and_recorded_too()
    {
        var (tenant, cashier, _) = await TillAsync("refused-override");

        using var response = await cashier.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 1m, unitPriceOverride = 0.01m },
            },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.AuthorizationRefused);

            Assert.Contains("CanOverridePrice", entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task A_refused_attempt_writes_no_sale()
    {
        var (tenant, cashier, _) = await TillAsync("refused-no-sale");

        using var response = await cashier.PostIdempotentAsync("/api/v1/sales", new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            cartDiscountAmount = 0.50m,
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The audit entry saves on its own, outside any transaction — which is only safe
            // because nothing else is tracked at that point. If the refusal path ever starts
            // staging work before this check, that save would commit it.
            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Empty(await db.StockMovements.ToListAsync());
        });
    }

    [Fact]
    public async Task Quoting_the_same_basket_records_nothing()
    {
        var (tenant, cashier, _) = await TillAsync("refused-quote");

        using var response = await cashier.PostAsJsonAsync("/api/v1/sales/quote", new
        {
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            cartDiscountAmount = 0.50m,
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The register re-quotes on every keystroke. Auditing the quote would file an
            // entry per character typed into the discount box, and the log would be unusable
            // by the end of the first shift — the same reason a quote does not consume a grant.
            Assert.Empty(await db.AuditEntries.ToListAsync());
        });
    }

    private async Task<(TradingTenant Tenant, HttpClient Cashier, Guid CashierId)> TillAsync(string _)
    {
        var (_, tenant) = await factory.TradingTenantAsync();

        var cashier = await factory.CreateUserAsync(
            tenant.TenantId, "cashier@trading.test", TradingTenant.Password, RoleNames.Cashier, "Robin Vale");

        var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(
            tenant.Slug, "cashier@trading.test", TradingTenant.Password)).AccessToken);

        return (tenant, client, cashier.Id);
    }
}
