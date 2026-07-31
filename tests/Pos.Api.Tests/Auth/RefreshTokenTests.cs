using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// Rotation and family revocation — the part of the token design that turns a stolen
/// refresh token from an indefinite session into one detected use.
/// </summary>
[Collection(PosApiCollection.Name)]
public sealed class RefreshTokenTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task Refreshing_returns_a_new_pair_and_retires_the_old_token()
    {
        var tenant = await factory.CreateTenantAsync("rotate");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var original = await client.LoginAsync("rotate", "owner@example.com", Password);

        var refreshed = await client.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);

        var body = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var rotated = body.GetProperty("refreshToken").GetString();

        Assert.NotEqual(original.RefreshToken, rotated);

        // Every use spends the token. Otherwise a copy taken from a log or a proxy stays
        // valid for its whole lifetime, and nothing ever notices it is in two places.
        var replay = await client.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
    }

    [Fact]
    public async Task Reusing_a_rotated_token_revokes_the_whole_family()
    {
        var tenant = await factory.CreateTenantAsync("family-revoke");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var original = await client.LoginAsync("family-revoke", "owner@example.com", Password);

        var firstRotation = await client.RefreshAsync(original.RefreshToken);
        var current = (await firstRotation.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("refreshToken").GetString()!;

        // The thief replays the token they copied. It fails — but the point is what it
        // proves: two parties hold tokens from one chain, which only happens if it leaked.
        var replay = await client.RefreshAsync(original.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // So the legitimate holder's current token dies too. One unexpected logout for
        // them, against a thief who could otherwise refresh forever.
        var afterDetection = await client.RefreshAsync(current);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDetection.StatusCode);
    }

    [Fact]
    public async Task A_refresh_token_from_another_tenant_is_rejected()
    {
        var tenantA = await factory.CreateTenantAsync("refresh-a");
        var tenantB = await factory.CreateTenantAsync("refresh-b");

        await factory.CreateUserAsync(tenantA.Id, "a@example.com", Password, RoleNames.Owner);
        await factory.CreateUserAsync(tenantB.Id, "b@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var tokensA = await client.LoginAsync("refresh-a", "a@example.com", Password);

        // Repoint the prefix at tenant B while keeping tenant A's secret half. The lookup
        // then runs inside B, where this hash exists nowhere.
        var forged = Rewrite(tokensA.RefreshToken, tenantB.Id);

        var response = await client.RefreshAsync(forged);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("bm90LWEtZ3VpZA.c2VjcmV0")]
    public async Task Malformed_refresh_tokens_are_rejected_without_a_server_error(string token)
    {
        using var client = factory.CreateClient();

        var response = await client.RefreshAsync(token);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logging_out_kills_the_family()
    {
        var tenant = await factory.CreateTenantAsync("logout");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("logout", "owner@example.com", Password);

        client.WithBearer(tokens.AccessToken);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        var afterLogout = await client.RefreshAsync(tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, afterLogout.StatusCode);
    }

    /// <summary>Replaces a token's tenant prefix, keeping its secret half intact.</summary>
    private static string Rewrite(string token, Guid tenantId)
    {
        var secret = token[(token.IndexOf('.', StringComparison.Ordinal) + 1)..];
        var prefix = System.Buffers.Text.Base64Url.EncodeToString(tenantId.ToByteArray());

        return $"{prefix}.{secret}";
    }
}
