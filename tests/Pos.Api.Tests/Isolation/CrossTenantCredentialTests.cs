using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// Checklist item 7: tenant A's user cannot reach tenant B's data with valid credentials.
/// </summary>
/// <remarks>
/// <c>LoginTests.A_users_credentials_do_not_work_against_another_tenant</c> already proves
/// the refusal — A's password against B's slug is a 401. What is left, and what the two
/// identical tenants make testable, is the positive case: the same email and the same
/// password exist in both shops, and what decides which one a session belongs to is the
/// slug alone.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CrossTenantCredentialTests(PosApiFactory factory)
{
    [Fact]
    public async Task Identical_credentials_in_both_tenants_resolve_to_the_slug_that_was_named()
    {
        var world = await factory.IsolationWorldAsync();

        using var client = factory.CreateClient();

        var tokens = await client.LoginAsync(
            world.B.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password);

        client.WithBearer(tokens.AccessToken);

        // The credentials are ambiguous on their own; the tenant is not something the user
        // proved, it is something the request selected and the server then bound the session
        // to. So the claim has to say B.
        Assert.Equal(world.B.Id.ToString(), TestTokens.TenantOf(tokens.AccessToken));

        var body = await client.GetFromJsonAsync<JsonElement>("/api/v1/registers");

        var ids = body.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Order().ToArray();

        Assert.Equal(world.B.RegisterIds.Order(), ids);
    }

    [Fact]
    public async Task Logging_out_cannot_revoke_another_tenants_session()
    {
        var world = await factory.IsolationWorldAsync();

        using var asA = factory.CreateClient();

        var aTokens = await asA.LoginAsync(
            world.A.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password);

        using var asB = await factory.ClientForAsync(Actor.OwnerOfB, world);

        var loggedOut = await asB.PostAsJsonAsync(
            "/api/v1/auth/logout",
            new { refreshToken = aTokens.RefreshToken });

        // 204 whatever it is handed, on purpose: an endpoint that answered "no such token"
        // would be a way to test whether a refresh token found somewhere is still live. The
        // status code therefore proves nothing, and the assertion has to be on the effect —
        // which is why this endpoint had no cross-tenant coverage until now.
        Assert.Equal(HttpStatusCode.NoContent, loggedOut.StatusCode);

        var refreshed = await asA.RefreshAsync(aTokens.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
    }
}
