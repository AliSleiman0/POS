using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Security;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// Covers the 1.5 promise: a PIN is authentication to a trusted device, never authentication
/// on its own.
/// </summary>
[Collection(PosApiCollection.Name)]
public sealed class PinLoginTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";
    private const string CashierPin = "4821";

    [Fact]
    public async Task A_cashier_can_start_a_session_by_PIN_from_an_enrolled_till()
    {
        var tenant = await factory.CreateTenantAsync("pin-ok");
        var cashier = await factory.CreateUserAsync(
            tenant.Id, "cashier@example.com", Password, RoleNames.Cashier, "Cal Cashier");

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);
        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.Equal("Cal Cashier", body.GetProperty("user").GetProperty("displayName").GetString());
        Assert.Equal(RoleNames.Cashier, body.GetProperty("user").GetProperty("role").GetString());
    }

    [Fact]
    public async Task A_PIN_session_is_bound_to_the_till_it_was_started_from()
    {
        var tenant = await factory.CreateTenantAsync("pin-register-claim");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);
        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = body.GetProperty("accessToken").GetString()!;

        // Which till rang a sale is not a detail the client gets to assert later — it is
        // fixed at login, in a signed token, because it is what a Z-report is grouped by.
        Assert.Equal(till.Id.ToString(), ReadClaim(accessToken, PosClaims.RegisterId));
    }

    [Fact]
    public async Task A_PIN_from_an_unenrolled_device_is_rejected_before_the_PIN_is_read()
    {
        var tenant = await factory.CreateTenantAsync("pin-unenrolled");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);
        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);

        // Well-formed and correctly tenant-prefixed, but no till was ever enrolled with it.
        var forged = OpaqueToken.Issue(tenant.Id);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, forged);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // And the correct PIN was not consumed as a failed attempt, which is the tell that
        // the request never reached the PIN check at all. If it had, an attacker without a
        // device token could still lock every cashier out of every till.
        Assert.Equal(0, await factory.AccessFailedCountAsync(tenant.Id, cashier.Id));
    }

    [Fact]
    public async Task A_PIN_from_a_till_whose_token_was_revoked_is_rejected()
    {
        var tenant = await factory.CreateTenantAsync("pin-revoked");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);
        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        var beforeRevocation = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.OK, beforeRevocation.StatusCode);

        // The lost-tablet case. Revocation has to bite immediately and with no further
        // action on the device, because the device is the thing we no longer control.
        await factory.RevokeRegisterAsync(tenant.Id, till.Id);

        var afterRevocation = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.Unauthorized, afterRevocation.StatusCode);
    }

    [Fact]
    public async Task A_device_token_from_another_tenant_admits_nobody()
    {
        var tenantA = await factory.CreateTenantAsync("pin-tenant-a");
        var tenantB = await factory.CreateTenantAsync("pin-tenant-b");

        var cashierA = await factory.CreateUserAsync(tenantA.Id, "cashier@example.com", Password, RoleNames.Cashier);
        await factory.SetPinAsync(tenantA.Id, cashierA.Id, CashierPin);

        var tillB = await factory.CreateEnrolledRegisterAsync(tenantB.Id);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, tillB.DeviceToken);

        // Shop B's till, shop A's cashier id. The token authenticates into tenant B, where
        // that user id does not exist — the query filter, not a check anyone wrote here.
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashierA.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_malformed_device_token_is_rejected()
    {
        var tenant = await factory.CreateTenantAsync("pin-malformed");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);
        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, "not-a-token");

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_ordinary_access_token_does_not_stand_in_for_a_device_token()
    {
        var tenant = await factory.CreateTenantAsync("pin-no-substitute");
        var owner = await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);
        await factory.SetPinAsync(tenant.Id, owner.Id, CashierPin);

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("pin-no-substitute", "owner@example.com", Password);
        client.WithBearer(tokens.AccessToken);

        // The EnrolledDevice policy names the DeviceToken scheme explicitly. Left to the
        // default scheme, any signed-in user would satisfy "enrolled device" and the whole
        // second factor would evaporate without a single test going red.
        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = owner.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_wrong_PINs_lock_the_cashier_out_and_the_response_says_until_when()
    {
        var tenant = await factory.CreateTenantAsync("pin-lockout");
        var cashier = await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier);

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);
        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        HttpResponseMessage? last = null;

        // Five is Identity's configured maximum. Four digits is 10,000 possibilities, so
        // without this cap the PIN is decoration.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            last?.Dispose();
            last = await client.PostAsJsonAsync(
                "/api/v1/auth/pin",
                new { userId = cashier.Id, pin = "0000" });
        }

        Assert.Equal(HttpStatusCode.Unauthorized, last!.StatusCode);

        var locked = await last.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("account-locked", locked.GetProperty("type").GetString()!, StringComparison.Ordinal);

        // The till can then say "try again at 14:05" instead of "no", which is the
        // difference between a cashier waiting and a cashier hammering the keypad.
        Assert.True(locked.TryGetProperty("lockoutEndsAt", out var until));
        Assert.True(until.GetDateTimeOffset() > DateTimeOffset.UtcNow);

        last.Dispose();

        // The correct PIN now fails too — otherwise the lockout counts failures without
        // actually stopping the sixth guess.
        var withCorrectPin = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = cashier.Id, pin = CashierPin });

        Assert.Equal(HttpStatusCode.Unauthorized, withCorrectPin.StatusCode);

        var body = await withCorrectPin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("account-locked", body.GetProperty("type").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_till_cannot_walk_the_staff_list_to_get_around_per_user_lockout()
    {
        var tenant = await factory.CreateTenantAsync("pin-ratelimit");
        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, till.DeviceToken);

        var statuses = new List<HttpStatusCode>();

        // Per-user lockout alone caps the wrong thing: an attacker holding one till spends
        // five attempts per cashier and moves on, and pin-eligible hands over the names to
        // move on to. The limiter counts attempts from the till, whoever they are aimed at.
        for (var attempt = 0; attempt < RateLimitingServiceCollectionExtensions.PinAttemptsPerWindow + 2; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/pin",
                new { userId = Guid.CreateVersion7(), pin = "0000" });

            statuses.Add(response.StatusCode);
        }

        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
        Assert.Equal(
            RateLimitingServiceCollectionExtensions.PinAttemptsPerWindow,
            statuses.Count(s => s != HttpStatusCode.TooManyRequests));
    }

    /// <summary>Reads one claim out of a JWT payload without validating it.</summary>
    private static string? ReadClaim(string accessToken, string claim)
    {
        var payload = accessToken.Split('.')[1];
        var json = System.Buffers.Text.Base64Url.DecodeFromChars(payload);

        return JsonSerializer.Deserialize<JsonElement>(json).TryGetProperty(claim, out var value)
            ? value.GetString()
            : null;
    }
}
