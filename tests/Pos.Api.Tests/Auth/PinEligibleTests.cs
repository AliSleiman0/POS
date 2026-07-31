using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// <c>GET /employees/pin-eligible</c> is readable from a tablet sitting on a counter, so what
/// it omits matters more than what it returns.
/// </summary>
[Collection(PosApiCollection.Name)]
public sealed class PinEligibleTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task It_returns_only_the_id_and_display_name_of_staff_who_have_a_PIN()
    {
        var tenant = await factory.CreateTenantAsync("eligible-shape");

        // Display names deliberately free of the words "Cashier" and "Owner", so the
        // role-leak assertions below are testing the response and not the fixture.
        var withPin = await factory.CreateUserAsync(
            tenant.Id, "cashier@example.com", Password, RoleNames.Cashier, "Cal Tillman");
        await factory.SetPinAsync(tenant.Id, withPin.Id, "4821");

        // Someone with no PIN: they log in with a password, and they should not be offered
        // on a screen that only accepts PINs.
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner, "Ann Prentice");

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        var response = await client.GetAsync(new Uri("/api/v1/employees/pin-eligible", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var raw = await response.Content.ReadAsStringAsync();
        var body = JsonSerializer.Deserialize<JsonElement>(raw);

        var employee = Assert.Single(body.EnumerateArray());
        Assert.Equal("Cal Tillman", employee.GetProperty("displayName").GetString());
        Assert.Equal(withPin.Id, employee.GetProperty("id").GetGuid());

        // Asserted on the serialised JSON, not on the DTO. A DTO assertion passes happily
        // while an extra property ships, because the type it checks is the type that grew.
        Assert.Equal(2, employee.EnumerateObject().Count());

        Assert.DoesNotContain("cashier@example.com", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("owner@example.com", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(RoleNames.Cashier, raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("pinHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phone", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task It_is_unreachable_without_a_device_token()
    {
        var tenant = await factory.CreateTenantAsync("eligible-noauth");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);
        await factory.SetPinAsync(tenant.Id, cashier.Id, "4821");

        using var client = factory.CreateClient();

        // The staff roster is not public just because it carries no email addresses. A name
        // list is the input to the PIN guessing this phase exists to make expensive.
        var response = await client.GetAsync(new Uri("/api/v1/employees/pin-eligible", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_till_sees_only_its_own_tenants_staff()
    {
        var tenantA = await factory.CreateTenantAsync("eligible-a");
        var tenantB = await factory.CreateTenantAsync("eligible-b");

        // Deliberately the same display name, so a leak is unmistakable rather than
        // something that has to be reasoned about from ids.
        var cashierA = await factory.CreateUserAsync(
            tenantA.Id, "same@example.com", Password, RoleNames.Cashier, "Sam Same");
        var cashierB = await factory.CreateUserAsync(
            tenantB.Id, "same@example.com", Password, RoleNames.Cashier, "Sam Same");

        await factory.SetPinAsync(tenantA.Id, cashierA.Id, "1111");
        await factory.SetPinAsync(tenantB.Id, cashierB.Id, "2222");

        var tillB = await factory.CreateEnrolledRegisterAsync(tenantB.Id);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, tillB.DeviceToken);

        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/employees/pin-eligible");

        var employee = Assert.Single(body.EnumerateArray());
        Assert.Equal(cashierB.Id, employee.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Setting_a_PIN_requires_the_employee_management_policy_and_a_well_formed_PIN()
    {
        var tenant = await factory.CreateTenantAsync("set-pin");

        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var asCashier = factory.CreateClient();
        var cashierTokens = await asCashier.LoginAsync("set-pin", "cashier@example.com", Password);
        asCashier.WithBearer(cashierTokens.AccessToken);

        // Whoever can set a PIN can set their own to something they know and then ring
        // sales as anybody. Same authority as managing employees.
        var byCashier = await asCashier.PostAsJsonAsync(
            $"/api/v1/employees/{cashier.Id}/set-pin",
            new { pin = "4821" });

        Assert.Equal(HttpStatusCode.Forbidden, byCashier.StatusCode);

        using var asOwner = factory.CreateClient();
        var ownerTokens = await asOwner.LoginAsync("set-pin", "owner@example.com", Password);
        asOwner.WithBearer(ownerTokens.AccessToken);

        var tooShort = await asOwner.PostAsJsonAsync(
            $"/api/v1/employees/{cashier.Id}/set-pin",
            new { pin = "12" });

        var notDigits = await asOwner.PostAsJsonAsync(
            $"/api/v1/employees/{cashier.Id}/set-pin",
            new { pin = "abcd" });

        var accepted = await asOwner.PostAsJsonAsync(
            $"/api/v1/employees/{cashier.Id}/set-pin",
            new { pin = "4821" });

        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notDigits.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, accepted.StatusCode);
    }
}
