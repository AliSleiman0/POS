using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Security;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

[Collection(PosApiCollection.Name)]
public sealed class RegisterEnrollmentTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task Enrollment_returns_the_device_token_once_and_it_is_never_readable_again()
    {
        var tenant = await factory.CreateTenantAsync("enroll-once");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("enroll-once", "owner@example.com", Password);
        client.WithBearer(tokens.AccessToken);

        var created = await client.PostAsJsonAsync("/api/v1/registers", new { name = "Front Till" });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var registerId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var enrolled = await client.PostAsJsonAsync($"/api/v1/registers/{registerId}/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);

        var deviceToken = (await enrolled.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("deviceToken").GetString();

        Assert.False(string.IsNullOrWhiteSpace(deviceToken));

        // Only the hash is kept, so a database dump does not hand over working tills — and
        // neither does any later read of this resource.
        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/registers");
        var raw = listed.ToString();

        Assert.DoesNotContain("deviceToken", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(deviceToken!, raw, StringComparison.Ordinal);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var stored = await db.Registers.FirstAsync(r => r.Id == registerId);

            Assert.Equal(OpaqueToken.Hash(deviceToken!), stored.DeviceTokenHash);
            Assert.NotEqual(deviceToken, stored.DeviceTokenHash);
        });
    }

    [Fact]
    public async Task The_device_token_is_tenant_prefixed_so_a_till_can_be_found_without_a_session()
    {
        var tenant = await factory.CreateTenantAsync("enroll-prefix");
        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        // A device token arrives from a till that has never logged in, so nothing else can
        // say which tenant to search. Without the prefix the lookup is a scan of every
        // tenant's registers — the unscoped read this phase exists to prevent.
        Assert.True(OpaqueToken.TryReadTenant(till.DeviceToken, out var tenantId));
        Assert.Equal(tenant.Id, tenantId);
    }

    [Fact]
    public async Task Re_enrolling_replaces_the_old_token()
    {
        var tenant = await factory.CreateTenantAsync("enroll-replace");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);
        await factory.SetPinAsync(tenant.Id, cashier.Id, "4821");

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        using var owner = factory.CreateClient();
        var tokens = await owner.LoginAsync("enroll-replace", "owner@example.com", Password);
        owner.WithBearer(tokens.AccessToken);

        var reissued = await owner.PostAsJsonAsync($"/api/v1/registers/{till.Id}/enroll", new { });
        Assert.Equal(HttpStatusCode.OK, reissued.StatusCode);

        // The route out of "the tablet was wiped and lost its token". The old one must stop
        // working, or a re-enroll doubles the number of devices that can reach the till.
        using var oldDevice = factory.CreateClient();
        oldDevice.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        var response = await oldDevice.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = "4821" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_cashier_cannot_enroll_a_till()
    {
        var tenant = await factory.CreateTenantAsync("enroll-rbac");
        await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("enroll-rbac", "cashier@example.com", Password);
        client.WithBearer(tokens.AccessToken);

        // Whoever can enroll a device can create a device that logs staff in. That is the
        // same authority as managing employees, and it is deliberately Owner-only.
        var enrolled = await client.PostAsJsonAsync($"/api/v1/registers/{till.Id}/enroll", new { });
        var listed = await client.GetAsync(new Uri("/api/v1/registers", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, enrolled.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, listed.StatusCode);
    }

    [Fact]
    public async Task Another_tenants_till_is_not_found_rather_than_forbidden()
    {
        var tenantA = await factory.CreateTenantAsync("enroll-cross-a");
        var tenantB = await factory.CreateTenantAsync("enroll-cross-b");

        var tillA = await factory.CreateEnrolledRegisterAsync(tenantA.Id);
        await factory.CreateUserAsync(tenantB.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("enroll-cross-b", "owner@example.com", Password);
        client.WithBearer(tokens.AccessToken);

        // 404, not 403. A 403 confirms the id exists somewhere, which turns this endpoint
        // into a way to test whether a guessed id belongs to another shop.
        var enrolled = await client.PostAsJsonAsync($"/api/v1/registers/{tillA.Id}/enroll", new { });
        Assert.Equal(HttpStatusCode.NotFound, enrolled.StatusCode);

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/registers");
        Assert.Empty(listed.EnumerateArray());
    }
}
