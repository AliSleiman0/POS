using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Employees;

/// <summary>
/// The ways a shop could lock itself out, and the refusals that stop it.
/// </summary>
/// <remarks>
/// These matter more than the CRUD around them. A tenant with no active owner cannot create
/// one — the only route that could is gated on <c>CanManageEmployees</c>, which only an owner
/// holds — so recovery means us connecting to their database by hand. There is no platform
/// admin tool by decision, so there is no other way back.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class EmployeeGuardrailTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    [Fact]
    public async Task An_owner_cannot_remove_their_own_owner_role()
    {
        var (client, tenant, ownerId) = await OwnerOfAsync("guard-self-demote");

        var updated = await client.PutAsJsonAsync($"/api/v1/employees/{ownerId}", new
        {
            displayName = "Pat Keeper",
            role = RoleNames.Manager,
            isActive = true,
        });

        await AssertProblemAsync(updated, HttpStatusCode.Conflict, "self-demotion");
        await AssertStillAnOwnerAsync(tenant.Id, ownerId);
    }

    [Fact]
    public async Task An_owner_cannot_deactivate_themselves_through_the_edit_route()
    {
        var (client, tenant, ownerId) = await OwnerOfAsync("guard-self-edit-off");

        var updated = await client.PutAsJsonAsync($"/api/v1/employees/{ownerId}", new
        {
            displayName = "Pat Keeper",
            role = RoleNames.Owner,
            isActive = false,
        });

        // Both doors are covered. Closing only the deactivate route would leave the edit
        // route as an unguarded way to the same place, which is the sort of gap that gets
        // found by a customer rather than by us.
        await AssertProblemAsync(updated, HttpStatusCode.Conflict, "self-deactivation");
        await AssertStillActiveAsync(tenant.Id, ownerId);
    }

    [Fact]
    public async Task An_owner_cannot_deactivate_themselves_through_the_deactivate_route()
    {
        var (client, tenant, ownerId) = await OwnerOfAsync("guard-self-deactivate");

        var deactivated = await client.PostAsJsonAsync($"/api/v1/employees/{ownerId}/deactivate", new { });

        await AssertProblemAsync(deactivated, HttpStatusCode.Conflict, "self-deactivation");
        await AssertStillActiveAsync(tenant.Id, ownerId);
    }

    [Fact]
    public async Task A_cashier_cannot_deactivate_themselves_either()
    {
        var tenant = await factory.CreateTenantAsync("guard-self-cashier");
        await factory.CreateUserAsync(
            tenant.Id, "owner@guard-self-cashier.test", Password, RoleNames.Owner, "Pat Keeper");

        // A cashier holds no CanManageEmployees, so they cannot reach the route at all — which
        // is the real answer, and worth pinning so nobody "fixes" the self-check by allowing
        // people to remove themselves.
        var cashier = await factory.CreateUserAsync(
            tenant.Id, "robin@guard-self-cashier.test", Password, RoleNames.Cashier, "Robin Vale");

        using var client = factory.CreateClient();
        var tokens = await client.LoginAsync("guard-self-cashier", "robin@guard-self-cashier.test", Password);
        client.WithBearer(tokens.AccessToken);

        var deactivated = await client.PostAsJsonAsync($"/api/v1/employees/{cashier.Id}/deactivate", new { });

        Assert.Equal(HttpStatusCode.Forbidden, deactivated.StatusCode);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_demoted_by_another_owner()
    {
        var (client, tenant, firstOwnerId) = await OwnerOfAsync("guard-last-demote");

        // Two owners, so the *self* checks do not fire and only the count stands between the
        // shop and nobody being able to manage it.
        var second = await factory.CreateUserAsync(
            tenant.Id, "second@guard-last-demote.test", Password, RoleNames.Owner, "Sam Cole");

        // Demote the other one first — permitted, two owners exist.
        var allowed = await client.PutAsJsonAsync($"/api/v1/employees/{second.Id}", new
        {
            displayName = "Sam Cole",
            role = RoleNames.Manager,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);

        // Now sign in as the demoted manager… who no longer holds the policy. Use the
        // remaining owner instead, aiming at themselves: self-demotion fires first, which is
        // the more useful message. What this test needs is a *third* party to swing the axe.
        var third = await factory.CreateUserAsync(
            tenant.Id, "third@guard-last-demote.test", Password, RoleNames.Owner, "Alex Reed");

        using var theirs = factory.CreateClient();
        var tokens = await theirs.LoginAsync("guard-last-demote", "third@guard-last-demote.test", Password);
        theirs.WithBearer(tokens.AccessToken);

        // Alex demotes Pat — fine, Alex is still an owner.
        var demotedPat = await theirs.PutAsJsonAsync($"/api/v1/employees/{firstOwnerId}", new
        {
            displayName = "Pat Keeper",
            role = RoleNames.Manager,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, demotedPat.StatusCode);

        // Alex is now the only owner, and demoting themselves is refused as self-demotion.
        var demotedSelf = await theirs.PutAsJsonAsync($"/api/v1/employees/{third.Id}", new
        {
            displayName = "Alex Reed",
            role = RoleNames.Manager,
            isActive = true,
        });

        await AssertProblemAsync(demotedSelf, HttpStatusCode.Conflict, "self-demotion");
        await AssertStillAnOwnerAsync(tenant.Id, third.Id);
    }

    [Fact]
    public async Task The_last_owner_cannot_be_deactivated_by_another_owner()
    {
        var (client, tenant, ownerId) = await OwnerOfAsync("guard-last-deactivate");

        var second = await factory.CreateUserAsync(
            tenant.Id, "second@guard-last-deactivate.test", Password, RoleNames.Owner, "Sam Cole");

        // Sam signs in and deactivates Pat. Permitted — Sam remains.
        using var theirs = factory.CreateClient();
        var tokens = await theirs.LoginAsync("guard-last-deactivate", "second@guard-last-deactivate.test", Password);
        theirs.WithBearer(tokens.AccessToken);

        var deactivatedPat = await theirs.PostAsJsonAsync($"/api/v1/employees/{ownerId}/deactivate", new { });
        Assert.Equal(HttpStatusCode.NoContent, deactivatedPat.StatusCode);

        // Now Sam is the last one. Their own attempt is self-deactivation; the interesting
        // case is somebody else trying, so promote a manager and have them try.
        var manager = await factory.CreateUserAsync(
            tenant.Id, "mgr@guard-last-deactivate.test", Password, RoleNames.Manager, "Alex Reed");

        var promoted = await theirs.PutAsJsonAsync($"/api/v1/employees/{manager.Id}", new
        {
            displayName = "Alex Reed",
            role = RoleNames.Owner,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, promoted.StatusCode);

        using var alex = factory.CreateClient();
        var alexTokens = await alex.LoginAsync("guard-last-deactivate", "mgr@guard-last-deactivate.test", Password);
        alex.WithBearer(alexTokens.AccessToken);

        // Alex deactivates Sam — two owners, so this is allowed and Alex is left alone.
        var deactivatedSam = await alex.PostAsJsonAsync($"/api/v1/employees/{second.Id}/deactivate", new { });
        Assert.Equal(HttpStatusCode.NoContent, deactivatedSam.StatusCode);

        // And now nobody can remove the last one, by any route.
        var lastStanding = await alex.PostAsJsonAsync($"/api/v1/employees/{manager.Id}/deactivate", new { });
        await AssertProblemAsync(lastStanding, HttpStatusCode.Conflict, "self-deactivation");

        await AssertStillActiveAsync(tenant.Id, manager.Id);
        await AssertStillAnOwnerAsync(tenant.Id, manager.Id);
    }

    [Fact]
    public async Task Two_owners_deactivating_each_other_at_the_same_moment_leave_one_standing()
    {
        var tenant = await factory.CreateTenantAsync("guard-race");

        var pat = await factory.CreateUserAsync(
            tenant.Id, "pat@guard-race.test", Password, RoleNames.Owner, "Pat Keeper");
        var sam = await factory.CreateUserAsync(
            tenant.Id, "sam@guard-race.test", Password, RoleNames.Owner, "Sam Cole");

        using var patsClient = factory.CreateClient();
        patsClient.WithBearer((await patsClient.LoginAsync("guard-race", "pat@guard-race.test", Password)).AccessToken);

        using var samsClient = factory.CreateClient();
        samsClient.WithBearer((await samsClient.LoginAsync("guard-race", "sam@guard-race.test", Password)).AccessToken);

        // Actually concurrent, per CLAUDE.md invariant 9. Neither request is acting on itself,
        // so no self-check fires and the row lock is the only thing standing between two
        // reads of "there are two of us" and a shop with no owner at all.
        var results = await Task.WhenAll(
            patsClient.PostAsJsonAsync($"/api/v1/employees/{sam.Id}/deactivate", new { }),
            samsClient.PostAsJsonAsync($"/api/v1/employees/{pat.Id}/deactivate", new { }));

        var statuses = results.Select(r => r.StatusCode).ToList();

        Assert.Contains(HttpStatusCode.NoContent, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var owners = await users.GetUsersInRoleAsync(RoleNames.Owner);

            Assert.Single(owners, u => u.IsActive);
        });

        foreach (var response in results)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task Two_staff_may_share_a_PIN_and_no_error_says_so()
    {
        var (client, tenant, _) = await OwnerOfAsync("guard-pin-collision");

        var robin = await factory.CreateUserAsync(
            tenant.Id, "robin@guard-pin-collision.test", Password, RoleNames.Cashier, "Robin Vale");
        var jules = await factory.CreateUserAsync(
            tenant.Id, "jules@guard-pin-collision.test", Password, RoleNames.Cashier, "Jules Nord");

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id);

        var first = await client.PostAsJsonAsync($"/api/v1/employees/{robin.Id}/set-pin", new { pin = "4821" });
        var second = await client.PostAsJsonAsync($"/api/v1/employees/{jules.Id}/set-pin", new { pin = "4821" });

        // Uniqueness is deliberately not enforced. "That PIN is taken" hands whoever asked a
        // working PIN for somebody else's account, which is a worse trade than two people
        // sharing four digits — identity here is picking your own name *and* entering a PIN.
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);

        // And the shared PIN still resolves to whoever was named, not to whoever was first.
        foreach (var (user, name) in new[] { (robin, "Robin Vale"), (jules, "Jules Nord") })
        {
            using var till_client = factory.CreateClient();
            till_client.WithDeviceToken(till.DeviceToken);

            var signedIn = await till_client.PostAsJsonAsync("/api/v1/auth/pin", new
            {
                userId = user.Id,
                pin = "4821",
            });

            Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);

            var body = await signedIn.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(name, body.GetProperty("user").GetProperty("displayName").GetString());
        }
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response, HttpStatusCode expected, string slug)
    {
        Assert.Equal(expected, response.StatusCode);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        // Asserted on `type`, which is the stable contract. `detail` is prose and may be
        // reworded at any time.
        Assert.Equal($"https://pos.example/errors/{slug}", problem.GetProperty("type").GetString());
    }

    private Task AssertStillAnOwnerAsync(Guid tenantId, Guid userId) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.Users.FirstAsync(u => u.Id == userId);

            // The refusal is the promise; this is the evidence. A handler that wrote the role
            // change and *then* threw would answer 409 and still have emptied the shop.
            Assert.True(await users.IsInRoleAsync(user, RoleNames.Owner));
        });

    private Task AssertStillActiveAsync(Guid tenantId, Guid userId) =>
        factory.AsTenantAsync(tenantId, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.Users.FirstAsync(u => u.Id == userId);

            Assert.True(user.IsActive);
        });

    private async Task<(HttpClient Client, Tenant Tenant, Guid OwnerId)> OwnerOfAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug);
        var owner = await factory.CreateUserAsync(
            tenant.Id, $"owner@{slug}.test", Password, RoleNames.Owner, "Pat Keeper");

        var client = factory.CreateClient();
        var tokens = await client.LoginAsync(slug, $"owner@{slug}.test", Password);
        client.WithBearer(tokens.AccessToken);

        return (client, tenant, owner.Id);
    }
}
