using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

[Collection(PosApiCollection.Name)]
public sealed class MeEndpointTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task Me_returns_the_users_policies_and_the_tenants_settings()
    {
        var tenant = await factory.CreateTenantAsync("me-ok", "Corner Shop");
        await factory.CreateUserAsync(tenant.Id, "cashier@example.com", Password, RoleNames.Cashier, "Sam Cashier");

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("me-ok", "cashier@example.com", Password);

        var response = await client.WithBearer(tokens.AccessToken).GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Sam Cashier", body.GetProperty("user").GetProperty("displayName").GetString());
        Assert.Equal(RoleNames.Cashier, body.GetProperty("user").GetProperty("role").GetString());

        var policies = body.GetProperty("user").GetProperty("policies")
            .EnumerateArray().Select(p => p.GetString() ?? string.Empty).ToArray();

        // A cashier sells and nothing else. The UI uses this list to grey out controls;
        // the server re-checks every call, because a disabled button is a suggestion.
        Assert.Equal(["CanSell"], policies);

        var tenantSettings = body.GetProperty("tenant");
        Assert.Equal("Corner Shop", tenantSettings.GetProperty("name").GetString());
        Assert.Equal("EUR", tenantSettings.GetProperty("currencyCode").GetString());
        Assert.Equal("Inclusive", tenantSettings.GetProperty("taxMode").GetString());
    }

    [Fact]
    public async Task Me_without_a_token_is_unauthorized()
    {
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_token_signed_with_the_wrong_key_is_rejected()
    {
        using var client = factory.CreateClient();

        // Structurally a JWT, signed by somebody else. Validation is the signature, not
        // the shape.
        client.WithBearer(
            "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJzdWIiOiIxMjM0NTY3ODkwIiwidGVuYW50X2lkIjoiMDAwMDAwMDAtMDAwMC0wMDAwLTAwMDAtMDAwMDAwMDAwMDAxIn0." +
            "SflKxwRJSMeKKF2QT4fwpMeJf36POk6yJV_adQssw5c");

        var response = await client.GetAsync(new Uri("/api/v1/auth/me", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Health_endpoints_stay_anonymous()
    {
        using var client = factory.CreateClient();

        var live = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
