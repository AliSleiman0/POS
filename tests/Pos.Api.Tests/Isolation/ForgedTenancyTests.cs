using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// Checklist items 5 and 6: what happens when the client lies about which tenant it is in,
/// in the token and in the request body.
/// </summary>
[Collection(PosApiCollection.Name)]
public sealed class ForgedTenancyTests(PosApiFactory factory)
{
    private static readonly Uri Me = new("/api/v1/auth/me", UriKind.Relative);
    private static readonly Uri Registers = new("/api/v1/registers", UriKind.Relative);

    [Fact]
    public async Task A_token_carrying_no_tenant_is_rejected()
    {
        var world = await factory.IsolationWorldAsync();

        var token = TestTokens.Signed(
        [
            new Claim(JwtRegisteredClaimNames.Sub, world.B.OwnerId.ToString()),
            new Claim(PosClaims.Role, RoleNames.Owner),
        ]);

        // A token this server signed, carrying no tenant, means the issuing path has a bug.
        // Continuing would run the request with no tenant resolved, and "no tenant" must
        // never quietly become "any tenant".
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(token, Me));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task A_token_whose_tenant_is_unusable_is_rejected(string tenantClaim)
    {
        var world = await factory.IsolationWorldAsync();

        var token = TestTokens.Signed(
        [
            new Claim(JwtRegisteredClaimNames.Sub, world.B.OwnerId.ToString()),
            new Claim(PosClaims.TenantId, tenantClaim),
            new Claim(PosClaims.Role, RoleNames.Owner),
        ]);

        // The empty guid is in this list and not treated as "unset": it is a real value that
        // matches no row under the query filter but would match rows inserted with a blank
        // tenant, so it has to be refused rather than resolved.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(token, Me));
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_rejected()
    {
        var world = await factory.IsolationWorldAsync();

        var token = TestTokens.Signed(
            [
                new Claim(JwtRegisteredClaimNames.Sub, world.B.OwnerId.ToString()),
                new Claim(PosClaims.TenantId, world.B.Id.ToString()),
                new Claim(PosClaims.Role, RoleNames.Owner),
            ],
            signingKey: "a-different-key-that-is-also-long-enough-32");

        // Otherwise every claim below is a client-supplied string.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(token, Me));
    }

    [Fact]
    public async Task A_token_naming_a_tenant_its_user_does_not_belong_to_cannot_read_that_user()
    {
        var world = await factory.IsolationWorldAsync();

        var token = TestTokens.Signed(
        [
            new Claim(JwtRegisteredClaimNames.Sub, world.B.OwnerId.ToString()),
            new Claim(PosClaims.TenantId, world.A.Id.ToString()),
            new Claim(PosClaims.Role, RoleNames.Owner),
        ]);

        // Tenant A is resolved from the claim, and inside tenant A the subject does not
        // exist — the query filter hides B's owner. So the session cannot be established
        // even though the token validated.
        Assert.Equal(HttpStatusCode.Unauthorized, await StatusOf(token, Me));
    }

    [Fact]
    public async Task A_validly_signed_token_is_trusted_for_whatever_tenant_it_names()
    {
        var world = await factory.IsolationWorldAsync();

        var token = TestTokens.Signed(
        [
            new Claim(JwtRegisteredClaimNames.Sub, world.B.OwnerId.ToString()),
            new Claim(PosClaims.TenantId, world.A.Id.ToString()),
            new Claim(PosClaims.Role, RoleNames.Owner),
        ]);

        using var client = factory.CreateClient().WithBearer(token);

        var response = await client.GetAsync(Registers);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var returned = body.EnumerateArray().Select(e => e.GetProperty("id").GetGuid()).Order().ToArray();

        // This test records a limit, not a guarantee, and it is here so the limit is written
        // down somewhere that cannot go stale.
        //
        // /registers never loads the user — the policy only needs the role claim — so unlike
        // /auth/me above there is nothing to catch the mismatch, and the request reads tenant
        // A's tills. That is not a hole an outsider can reach: producing this token needs the
        // signing key, and anyone holding it can mint any claim they like. What it means is
        // that **the signing key is the tenancy boundary** for read paths that do not touch
        // the user row.
        //
        // Closing it would mean re-checking on every request that the subject exists in the
        // tenant the token names — one indexed lookup, which also expires a deactivated
        // user's token early. That is a change to how sessions are validated, not to this
        // suite; it is written up in DECISIONS.md. If it is ever made, this test goes red,
        // and that is the intended way to find out.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(world.A.RegisterIds.Order(), returned);
    }

    [Fact]
    public async Task A_tenant_id_in_the_request_body_is_never_honoured()
    {
        // Its own tenants rather than the shared world: this is the one test in the suite
        // that successfully creates a row, and the world's collection assertions are exact.
        var victim = await factory.CreateTenantAsync("iso-body-victim");
        var caller = await factory.CreateTenantAsync("iso-body-caller");

        await factory.CreateUserAsync(
            caller.Id, "owner@example.com", TwoTenantWorld.Password, RoleNames.Owner);

        using var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync("iso-body-caller", "owner@example.com", TwoTenantWorld.Password))
            .AccessToken);

        var response = await client.PostAsJsonAsync(
            "/api/v1/registers",
            new { name = "Smuggled", tenantId = victim.Id });

        // Not rejected — ignored. CreateRegisterRequest has no TenantId to bind to, so the
        // property goes nowhere, and the interceptor stamps the tenant from the validated
        // token regardless of what the body asked for (CLAUDE.md invariant 2). The guarantee
        // worth having is "never honoured"; rejecting unknown properties outright would make
        // every client break on the first field the server has not heard of yet.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(victim.Id, async services =>
            Assert.Null(await TillsIn(services).FirstOrDefaultAsync(r => r.Id == id)));

        await factory.AsTenantAsync(caller.Id, async services =>
            Assert.NotNull(await TillsIn(services).FirstOrDefaultAsync(r => r.Id == id)));
    }

    private static DbSet<Core.Entities.Register> TillsIn(IServiceProvider services) =>
        services.GetRequiredService<AppDbContext>().Registers;

    private async Task<HttpStatusCode> StatusOf(string token, Uri url)
    {
        using var client = factory.CreateClient().WithBearer(token);

        return (await client.GetAsync(url)).StatusCode;
    }
}
