using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Settings;

/// <summary>
/// The shop's own configuration, and the one write in this API with no safety net under it.
/// </summary>
/// <remarks>
/// <c>Tenant</c> is deliberately not tenant-owned — it is the list of tenants, and login has to
/// resolve a row in it before any tenant is known — so it carries no query filter, no RLS
/// policy and no interceptor guard. Every other write in the application has three layers
/// beneath it; this one has a <c>Where</c> clause. That is what
/// <see cref="A_put_only_ever_touches_the_calling_tenants_row"/> exists for.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SettingsTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task An_owner_can_change_the_receipt_footer_and_the_rounding_rule()
    {
        var (client, tenant) = await OwnerOfAsync("settings-edit");

        using var response = await client.PutAsJsonAsync("/api/v1/settings", Update(new
        {
            receiptFooter = "Thanks for shopping with us",
            cashRoundingIncrement = 0.05m,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Thanks for shopping with us", body.GetProperty("receiptFooter").GetString());

        // Until now these were reachable only through tools/Pos.Seed, which is a dev tool that
        // is not shipped — so a customer could not change their own receipt footer at all.
        var read = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        Assert.Equal("Thanks for shopping with us", read.GetProperty("receiptFooter").GetString());
        Assert.Equal(0.05m, read.GetProperty("cashRoundingIncrement").GetDecimal());

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shop = await db.Tenants.FirstAsync(t => t.Id == tenant.Id);

            Assert.Equal("Thanks for shopping with us", shop.ReceiptFooter);
        });
    }

    [Fact]
    public async Task A_put_only_ever_touches_the_calling_tenants_row()
    {
        var (clientA, tenantA) = await OwnerOfAsync("settings-victim");
        var (clientB, _) = await OwnerOfAsync("settings-attacker");

        await clientA.PutAsJsonAsync("/api/v1/settings", Update(new { name = "Victim Shop" }));

        // No id is accepted anywhere on this route, so the only way to reach another tenant is
        // for the Where clause to be missing — and if it were, this PUT would rename whichever
        // row the query happened to return first. Nothing else in the stack would notice.
        using var response = await clientB.PutAsJsonAsync("/api/v1/settings", Update(new
        {
            name = "Attacker Shop",
            receiptFooter = "Owned",
            tenantId = tenantA.Id,
            id = tenantA.Id,
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.AsTenantAsync(tenantA.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var victim = await db.Tenants.FirstAsync(t => t.Id == tenantA.Id);

            Assert.Equal("Victim Shop", victim.Name);
            Assert.Null(victim.ReceiptFooter);
        });
    }

    [Fact]
    public async Task A_get_only_ever_reads_the_calling_tenants_row()
    {
        var (clientA, _) = await OwnerOfAsync("settings-read-a");
        var (clientB, _) = await OwnerOfAsync("settings-read-b");

        await clientA.PutAsJsonAsync("/api/v1/settings", Update(new { name = "Shop A" }));
        await clientB.PutAsJsonAsync("/api/v1/settings", Update(new { name = "Shop B" }));

        var readA = await clientA.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        var readB = await clientB.GetFromJsonAsync<JsonElement>("/api/v1/settings");

        Assert.Equal("Shop A", readA.GetProperty("name").GetString());
        Assert.Equal("Shop B", readB.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Each_changed_setting_is_audited_separately()
    {
        var (client, tenant) = await OwnerOfAsync("settings-audit");

        using var response = await client.PutAsJsonAsync("/api/v1/settings", Update(new
        {
            receiptFooter = "Returns within 30 days",
            taxNumber = "IE1234567X",
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entries = await db.AuditEntries
                .Where(a => a.Action == AuditAction.SettingsChanged)
                .ToListAsync();

            // One per key, matching the phase table's "key, before, after". A single entry
            // carrying the whole object would make a reader diff two blobs to discover that
            // the footer changed.
            Assert.Equal(2, entries.Count);
            Assert.Contains(entries, e => e.After!.Contains("receiptFooter", StringComparison.Ordinal));
            Assert.Contains(entries, e => e.After!.Contains("IE1234567X", StringComparison.Ordinal));

            // And the before side is populated, which is the half that makes an entry useful.
            Assert.All(entries, e => Assert.NotNull(e.Before));
        });
    }

    [Fact]
    public async Task Saving_with_nothing_changed_records_nothing()
    {
        var (client, tenant) = await OwnerOfAsync("settings-noop");

        await client.PutAsJsonAsync("/api/v1/settings", Update(new { receiptFooter = "Same" }));
        await client.PutAsJsonAsync("/api/v1/settings", Update(new { receiptFooter = "Same" }));

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Pressing Save twice is not an event. An entry per press would bury the changes
            // that matter under rows recording that nothing happened.
            var entries = await db.AuditEntries
                .Where(a => a.Action == AuditAction.SettingsChanged)
                .ToListAsync();

            Assert.Single(entries);
        });
    }

    [Fact]
    public async Task Tax_mode_can_be_changed_before_the_first_sale()
    {
        var (client, _) = await OwnerOfAsync("settings-taxmode-open");

        using var response = await client.PutAsJsonAsync(
            "/api/v1/settings",
            Update(new { taxMode = nameof(TaxMode.Exclusive) }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            nameof(TaxMode.Exclusive),
            (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("taxMode").GetString());
    }

    [Fact]
    public async Task Tax_mode_is_refused_once_the_shop_has_traded()
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

        // Inclusive, because TradingTenant trades Exclusive — sending back what a shop already
        // has is not a change, and this test needs a real one.
        using var response = await client.PutAsJsonAsync(
            "/api/v1/settings",
            Update(new { taxMode = nameof(TaxMode.Inclusive) }));

        // Refused, not warned about. Changing it would reinterpret every price already
        // recorded — every historical total silently becomes a different number, and nothing
        // in the data says when the meaning changed.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(
            "https://pos.example/errors/tax-mode-locked",
            problem.GetProperty("type").GetString());

        // And the read side says so up front, so a screen can render the control as
        // read-only-with-a-reason rather than offering one that 409s.
        var read = await client.GetFromJsonAsync<JsonElement>("/api/v1/settings");
        Assert.True(read.GetProperty("taxModeLocked").GetBoolean());
    }

    [Fact]
    public async Task Resending_the_current_tax_mode_after_trading_is_fine()
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

        // The lock is on *changing* it. A form that posts every field back would otherwise be
        // unable to edit the footer of a shop that had ever sold anything.
        using var response = await client.PutAsJsonAsync("/api/v1/settings", Update(new
        {
            taxMode = nameof(TaxMode.Exclusive),
            receiptFooter = "Still editable",
        }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_may_read_the_settings_but_not_change_them()
    {
        var tenant = await factory.CreateTenantAsync("settings-cashier");
        await factory.CreateUserAsync(
            tenant.Id, "robin@settings-cashier.test", Password, RoleNames.Cashier, "Robin Vale");

        using var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync("settings-cashier", "robin@settings-cashier.test", Password)).AccessToken);

        // The till needs the currency and the rounding rule to render a total, so the read is
        // CanSell. Changing them is not a cashier's business.
        using var read = await client.GetAsync(new Uri("/api/v1/settings", UriKind.Relative));
        using var written = await client.PutAsJsonAsync("/api/v1/settings", Update(new { }));

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, written.StatusCode);
    }

    [Theory]
    [InlineData("name", "")]
    [InlineData("cashRoundingIncrement", -0.05)]
    [InlineData("cashRoundingIncrement", 5)]
    public async Task A_malformed_value_names_the_field(string field, object value)
    {
        var (client, _) = await OwnerOfAsync($"settings-invalid-{field.ToLowerInvariant()}-{Math.Abs(value.GetHashCode() % 1000)}");

        var body = Update(new { });
        body[field] = value;

        using var response = await client.PutAsJsonAsync("/api/v1/settings", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    /// <summary>A complete valid body, with the given fields overridden.</summary>
    /// <remarks>
    /// The route is a PUT, so it takes the whole object — a partial body would leave the
    /// omitted fields ambiguous between "unchanged" and "cleared".
    /// </remarks>
    private static Dictionary<string, object?> Update(object overrides)
    {
        var body = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = "Test Shop",
            ["taxMode"] = nameof(TaxMode.Inclusive),
            ["cashRoundingIncrement"] = 0m,
            ["addressLine"] = null,
            ["taxNumber"] = null,
            ["receiptHeader"] = null,
            ["receiptFooter"] = null,
        };

        foreach (var property in overrides.GetType().GetProperties())
        {
            body[property.Name] = property.GetValue(overrides);
        }

        return body;
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
