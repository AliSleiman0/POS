using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

[Collection(PosApiCollection.Name)]
public sealed class LoginTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task Valid_credentials_return_a_token_pair_and_the_users_policies()
    {
        var tenant = await factory.CreateTenantAsync("login-ok");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner, "Ann Owner");

        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "login-ok", email = "owner@example.com", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("refreshToken").GetString()));
        Assert.Equal(900, body.GetProperty("expiresIn").GetInt32());

        var user = body.GetProperty("user");
        Assert.Equal("Ann Owner", user.GetProperty("displayName").GetString());
        Assert.Equal(RoleNames.Owner, user.GetProperty("role").GetString());

        var policies = user.GetProperty("policies").EnumerateArray().Select(p => p.GetString()).ToArray();
        Assert.Contains("CanViewMargins", policies);
        Assert.Contains("CanSell", policies);
    }

    [Fact]
    public async Task The_refresh_token_is_tenant_prefixed_so_it_can_be_looked_up_without_a_session()
    {
        var tenant = await factory.CreateTenantAsync("login-prefix");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("login-prefix", "owner@example.com", Password);

        // A refresh token arrives with no access token, so nothing else can say which
        // tenant to search. The prefix narrows; the random half authenticates.
        Assert.True(Pos.Core.Security.OpaqueToken.TryReadTenant(tokens.RefreshToken, out var tenantId));
        Assert.Equal(tenant.Id, tenantId);
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_tenant_are_indistinguishable()
    {
        var tenant = await factory.CreateTenantAsync("login-oracle");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Owner);

        using var client = factory.CreateClient();

        var wrongPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "login-oracle", email = "owner@example.com", password = "wrong-password-1" });

        var unknownTenant = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "no-such-shop", email = "owner@example.com", password = Password });

        var unknownUser = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "login-oracle", email = "nobody@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownTenant.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknownUser.StatusCode);

        // Identical bodies too. "No such tenant" versus "wrong password" tells an attacker
        // which shops exist and which addresses have accounts at them — that is a target
        // list, assembled one request at a time.
        //
        // Compared field by field rather than as raw strings: problem+json carries a
        // per-request traceId, which differs by design and says nothing about the account.
        var first = await ReadProblemAsync(wrongPassword);
        var second = await ReadProblemAsync(unknownTenant);
        var third = await ReadProblemAsync(unknownUser);

        Assert.Equal(first, second);
        Assert.Equal(first, third);
        Assert.Contains("invalid-credentials", first, StringComparison.Ordinal);
    }

    /// <summary>The parts of a problem response a caller could learn anything from.</summary>
    private static async Task<string> ReadProblemAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return string.Join('|', new[] { "type", "title", "status", "detail" }
            .Select(name => body.TryGetProperty(name, out var value) ? value.ToString() : "(absent)"));
    }

    [Fact]
    public async Task A_users_credentials_do_not_work_against_another_tenant()
    {
        // Same person, same password, two shops. The slug is a selector, not a credential:
        // presenting shop B's slug must not admit shop A's user.
        var tenantA = await factory.CreateTenantAsync("cross-a");
        var tenantB = await factory.CreateTenantAsync("cross-b");

        await factory.CreateUserAsync(tenantA.Id, "shared@example.com", Password, RoleNames.Owner, "Ann at A");
        await factory.CreateUserAsync(tenantB.Id, "shared@example.com", "Different-Horse-9", RoleNames.Cashier, "Ann at B");

        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "cross-b", email = "shared@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_slug_is_normalised_so_casing_and_spacing_do_not_break_login()
    {
        var tenant = await factory.CreateTenantAsync("normalise-me");
        await factory.CreateUserAsync(tenant.Id, "owner@example.com", Password, RoleNames.Manager);

        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "  Normalise Me  ", email = "owner@example.com", password = Password });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_user_cannot_log_in()
    {
        var tenant = await factory.CreateTenantAsync("deactivated");
        var user = await factory.CreateUserAsync(tenant.Id, "gone@example.com", Password, RoleNames.Cashier);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<Pos.Data.AppDbContext>();
            var stored = await db.Users.FindAsync(user.Id);
            stored!.IsActive = false;
            await db.SaveChangesAsync();
        });

        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "deactivated", email = "gone@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Repeated_failures_lock_the_account_out()
    {
        var tenant = await factory.CreateTenantAsync("lockout");
        await factory.CreateUserAsync(tenant.Id, "target@example.com", Password, RoleNames.Cashier);

        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 5; attempt++)
        {
            await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { tenantSlug = "lockout", email = "target@example.com", password = "wrong-password-1" });
        }

        // The correct password now fails too. Without this an attacker gets unlimited
        // guesses, and the password policy is the only thing standing in the way.
        var withCorrectPassword = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug = "lockout", email = "target@example.com", password = Password });

        Assert.Equal(HttpStatusCode.Unauthorized, withCorrectPassword.StatusCode);
    }
}
