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

/// <summary>
/// <c>POST /auth/override</c> — a manager's PIN authorising one action on somebody else's
/// session.
/// </summary>
/// <remarks>
/// The flow exists because the two obvious alternatives are both wrong: swapping the session
/// attributes the sale to the manager, and handing the cashier the manager's role is a standing
/// grant with no end. What is tested here is that this third way is not a fourth mistake — that
/// it cannot mint anything but a discount or an override, cannot be used without the till's
/// device token, and counts a wrong PIN the way the PIN screen does.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class OverrideGrantTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/auth/override";
    private const string Password = "Correct-Horse-9";
    private const string ManagerPin = "7391";

    [Fact]
    public async Task A_manager_can_authorise_a_discount_from_an_enrolled_till()
    {
        var (tenant, till, manager) = await ManagerAtATillAsync("grant-ok");

        using var client = TillClient(till.DeviceToken);

        using var response = await client.PostAsJsonAsync(
            Route,
            new { userId = manager.Id, pin = ManagerPin, policies = new[] { "CanApplyDiscount" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(string.IsNullOrWhiteSpace(body.GetProperty("grant").GetString()));
        Assert.Equal("Sam Cole", body.GetProperty("authorizedByName").GetString());
        Assert.Equal(manager.Id, body.GetProperty("authorizedById").GetGuid());
        Assert.Equal("CanApplyDiscount", body.GetProperty("policies")[0].GetString());

        // Stored as a digest, exactly as a refresh token and a device token are. A leaked
        // database has to hand an attacker hashes, not usable authorisations.
        var presented = body.GetProperty("grant").GetString()!;

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var grant = await db.OverrideGrants.SingleAsync(g => g.UserId == manager.Id);

            Assert.Equal(OpaqueToken.Hash(presented), grant.TokenHash);
            Assert.Null(grant.ConsumedAt);
            Assert.Equal(till.Id, grant.RegisterId);
        });
    }

    [Fact]
    public async Task A_cashier_cannot_authorise_their_own_discount()
    {
        // The point of the whole mechanism. A cashier with a valid PIN is still not somebody
        // who can approve a discount, and the refusal has to survive them knowing that.
        var tenant = await factory.CreateTenantAsync("grant-cashier");
        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        var cashier = await factory.CreateUserAsync(
            tenant.Id, "cashier@example.com", Password, RoleNames.Cashier, "Robin Vale");

        await factory.SetPinAsync(tenant.Id, cashier.Id, ManagerPin);

        using var client = TillClient(till.DeviceToken);

        using var response = await client.PostAsJsonAsync(
            Route,
            new { userId = cashier.Id, pin = ManagerPin, policies = new[] { "CanApplyDiscount" } });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Contains(
            "override-not-permitted",
            body.GetProperty("type").GetString()!,
            StringComparison.Ordinal);

        await AssertNoGrantsAsync(tenant.Id);
    }

    [Fact]
    public async Task A_policy_outside_the_allow_list_is_refused_before_the_PIN_is_read()
    {
        // Without the allow-list this endpoint is a general elevation mechanism: a manager's
        // PIN would mint CanManageEmployees, and whoever held the grant could set their own PIN
        // on the owner's account. Checked before the PIN so it cannot be used to probe one.
        var (tenant, till, manager) = await ManagerAtATillAsync("grant-allowlist");

        using var client = TillClient(till.DeviceToken);

        using var response = await client.PostAsJsonAsync(
            Route,
            new { userId = manager.Id, pin = "0000", policies = new[] { "CanManageEmployees" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("errors").TryGetProperty("policies", out _));

        // The wrong PIN was never checked, so it cost nothing: otherwise this route would be a
        // way to burn a manager's lockout budget without holding a single valid policy name.
        Assert.Equal(0, await factory.AccessFailedCountAsync(tenant.Id, manager.Id));
        await AssertNoGrantsAsync(tenant.Id);
    }

    [Fact]
    public async Task An_empty_policy_list_is_refused()
    {
        // A grant that authorises nothing is not a harmless no-op — it is a row that looks like
        // an authorisation in a table whose whole purpose is to record them.
        var (tenant, till, manager) = await ManagerAtATillAsync("grant-empty");

        using var client = TillClient(till.DeviceToken);

        using var response = await client.PostAsJsonAsync(
            Route,
            new { userId = manager.Id, pin = ManagerPin, policies = Array.Empty<string>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNoGrantsAsync(tenant.Id);
    }

    [Fact]
    public async Task An_unenrolled_device_cannot_mint_a_grant()
    {
        // The device token is the second factor. A manager's PIN alone, typed from anywhere,
        // must not authorise anything — which is the same rule /auth/pin holds.
        var tenant = await factory.CreateTenantAsync("grant-unenrolled");
        var manager = await factory.CreateUserAsync(
            tenant.Id, "manager@example.com", Password, RoleNames.Manager, "Sam Cole");

        await factory.SetPinAsync(tenant.Id, manager.Id, ManagerPin);

        // Well-formed and correctly tenant-prefixed, but no till was ever enrolled with it.
        using var client = TillClient(OpaqueToken.Issue(tenant.Id));

        using var response = await client.PostAsJsonAsync(
            Route,
            new { userId = manager.Id, pin = ManagerPin, policies = new[] { "CanApplyDiscount" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoGrantsAsync(tenant.Id);
    }

    [Fact]
    public async Task An_ordinary_access_token_does_not_stand_in_for_a_device_token()
    {
        // The EnrolledDevice policy names the DeviceToken scheme explicitly. Left to the
        // default scheme, any signed-in user would satisfy "enrolled device" — and a manager
        // could authorise their own overrides from a laptop at home.
        var (tenant, _, manager) = await ManagerAtATillAsync("grant-no-substitute");

        using var client = factory.CreateClient();
        client.WithBearer(
            (await client.LoginAsync("grant-no-substitute", "manager@example.com", Password)).AccessToken);

        using var response = await client.PostAsJsonAsync(
            Route,
            new { userId = manager.Id, pin = ManagerPin, policies = new[] { "CanApplyDiscount" } });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertNoGrantsAsync(tenant.Id);
    }

    [Fact]
    public async Task A_wrong_PIN_here_counts_toward_the_same_lockout_as_the_PIN_screen()
    {
        // Both routes verify a PIN through one shared helper, and this is what says so. A
        // forked copy that skipped AccessFailedAsync would leave an unlimited guessing oracle
        // behind the endpoint nobody thinks of as a login.
        var (tenant, till, manager) = await ManagerAtATillAsync("grant-lockout");

        using var client = TillClient(till.DeviceToken);

        for (var attempt = 0; attempt < 5; attempt++)
        {
            using var wrong = await client.PostAsJsonAsync(
                Route,
                new { userId = manager.Id, pin = "0000", policies = new[] { "CanApplyDiscount" } });

            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        // Locked out of the PIN screen too, by failures that were never spent there.
        using var pinLogin = await client.PostAsJsonAsync(
            "/api/v1/auth/pin",
            new { userId = manager.Id, pin = ManagerPin });

        Assert.Equal(HttpStatusCode.Unauthorized, pinLogin.StatusCode);

        var body = await pinLogin.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("account-locked", body.GetProperty("type").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_policies_can_be_carried_by_one_grant()
    {
        // A cart can hold a discount and a price override at once. Two grants would mean two
        // headers and two PIN entries for an authorisation the manager gave once.
        var (tenant, till, manager) = await ManagerAtATillAsync("grant-both");

        using var client = TillClient(till.DeviceToken);

        using var response = await client.PostAsJsonAsync(
            Route,
            new
            {
                userId = manager.Id,
                pin = ManagerPin,
                policies = new[] { "CanOverridePrice", "CanApplyDiscount" },
            });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var grant = await db.OverrideGrants.SingleAsync();

            Assert.Equal(["CanApplyDiscount", "CanOverridePrice"], grant.Policies);
        });
    }

    /// <summary>A manager with a PIN, standing at an enrolled till.</summary>
    private async Task<(Core.Entities.Tenant Tenant, EnrolledRegister Till, ApplicationUser Manager)>
        ManagerAtATillAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug);
        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        var manager = await factory.CreateUserAsync(
            tenant.Id, "manager@example.com", Password, RoleNames.Manager, "Sam Cole");

        await factory.SetPinAsync(tenant.Id, manager.Id, ManagerPin);

        return (tenant, till, manager);
    }

    private HttpClient TillClient(string deviceToken)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, deviceToken);

        return client;
    }

    /// <summary>Nothing was minted. A refusal that still wrote a row is not a refusal.</summary>
    private Task AssertNoGrantsAsync(Guid tenantId) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.OverrideGrants.ToListAsync());
        });
}
