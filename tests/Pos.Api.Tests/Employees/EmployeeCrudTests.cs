using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Employees;

/// <summary>
/// The lifecycle an owner has to be able to run without contacting us.
/// </summary>
/// <remarks>
/// Every test seeds its own throwaway tenant rather than using the shared world, because all
/// of them write. The world is read-only by rule — its exact-count assertions would otherwise
/// depend on the order xUnit happens to run classes in.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class EmployeeCrudTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";
    private const string StaffPassword = "Battery-Staple-7";

    [Fact]
    public async Task An_owner_can_create_a_cashier_who_can_then_sign_in()
    {
        var (client, _) = await OwnerOfAsync("emp-create");

        var created = await client.PostAsJsonAsync("/api/v1/employees", new
        {
            displayName = "Robin Vale",
            email = "robin@emp-create.test",
            role = RoleNames.Cashier,
            password = StaffPassword,
            pin = (string?)null,
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var body = await created.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Robin Vale", body.GetProperty("displayName").GetString());
        Assert.Equal(RoleNames.Cashier, body.GetProperty("role").GetString());
        Assert.True(body.GetProperty("isActive").GetBoolean());
        Assert.False(body.GetProperty("hasPin").GetBoolean());

        // The point of the whole endpoint: the person can actually get in afterwards. A
        // create that returns 201 and leaves an account nobody can use is the failure this
        // catches, and it is not visible from the response.
        using var theirs = factory.CreateClient();
        var tokens = await theirs.LoginAsync("emp-create", "robin@emp-create.test", StaffPassword);

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
    }

    [Fact]
    public async Task A_created_employee_belongs_to_the_calling_tenant()
    {
        var (client, tenant) = await OwnerOfAsync("emp-tenancy");

        // The exemption on this route's manifest row names this test. A write with no id in
        // the URL has no cross-tenant victim to reach for, so the tenancy question is where
        // the new row lands — and the interceptor, not the body, is what decides.
        var created = await client.PostAsJsonAsync("/api/v1/employees", new
        {
            displayName = "Jules Nord",
            email = "jules@emp-tenancy.test",
            role = RoleNames.Manager,
            password = StaffPassword,
            tenantId = Guid.NewGuid(),
        });

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.Users.FirstOrDefaultAsync(u => u.Id == id);

            Assert.NotNull(user);
            Assert.Equal(tenant.Id, user.TenantId);
        });
    }

    [Fact]
    public async Task The_list_shows_role_pin_and_status_and_includes_deactivated_staff()
    {
        var (client, tenant) = await OwnerOfAsync("emp-list");

        var cashier = await factory.CreateUserAsync(
            tenant.Id, "robin@emp-list.test", StaffPassword, RoleNames.Cashier, "Robin Vale");

        await factory.SetPinAsync(tenant.Id, cashier.Id, "4821");

        var gone = await factory.CreateUserAsync(
            tenant.Id, "old@emp-list.test", StaffPassword, RoleNames.Cashier, "Former Staff");

        var deactivated = await client.PostAsJsonAsync($"/api/v1/employees/{gone.Id}/deactivate", new { });
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        var listed = await client.GetFromJsonAsync<JsonElement>("/api/v1/employees");
        var rows = listed.EnumerateArray().ToList();

        var robin = rows.Single(r => r.GetProperty("id").GetGuid() == cashier.Id);
        Assert.Equal(RoleNames.Cashier, robin.GetProperty("role").GetString());
        Assert.True(robin.GetProperty("hasPin").GetBoolean());

        // Deactivated staff are listed by default, unlike the catalog's lists. There is no
        // reactivate route — you reactivate somebody by editing them — so hiding them would
        // make the only way back invisible.
        var former = rows.Single(r => r.GetProperty("id").GetGuid() == gone.Id);
        Assert.False(former.GetProperty("isActive").GetBoolean());

        // ...and the filter still exists for when an owner wants only current staff.
        var activeOnly = await client.GetFromJsonAsync<JsonElement>("/api/v1/employees?activeOnly=true");
        var activeIds = activeOnly.EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToList();

        Assert.DoesNotContain(gone.Id, activeIds);
        Assert.Contains(cashier.Id, activeIds);
    }

    [Fact]
    public async Task The_list_never_leaks_a_hash()
    {
        var (client, tenant) = await OwnerOfAsync("emp-no-hash");

        var cashier = await factory.CreateUserAsync(
            tenant.Id, "robin@emp-no-hash.test", StaffPassword, RoleNames.Cashier, "Robin Vale");

        await factory.SetPinAsync(tenant.Id, cashier.Id, "4821");

        var response = await client.GetAsync(new Uri("/api/v1/employees", UriKind.Relative));
        var raw = await response.Content.ReadAsStringAsync();

        // Asserted on the raw JSON rather than on a DTO, for the reason PinEligibleTests
        // gives: a DTO assertion passes happily while an extra property ships.
        Assert.DoesNotContain("pinHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passwordHash", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Editing_changes_the_name_and_the_role_together()
    {
        var (client, tenant) = await OwnerOfAsync("emp-edit");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "sam@emp-edit.test", StaffPassword, RoleNames.Cashier, "Sam Cole");

        var updated = await client.PutAsJsonAsync($"/api/v1/employees/{staff.Id}", new
        {
            displayName = "Samantha Cole",
            role = RoleNames.Manager,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        var body = await updated.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Samantha Cole", body.GetProperty("displayName").GetString());
        Assert.Equal(RoleNames.Manager, body.GetProperty("role").GetString());

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.Users.FirstAsync(u => u.Id == staff.Id);

            // The old role is removed, not merely joined by the new one. A user holding both
            // Cashier and Manager would satisfy the union of two policy sets, which is a
            // privilege the owner never granted.
            Assert.True(await users.IsInRoleAsync(user, RoleNames.Manager));
            Assert.False(await users.IsInRoleAsync(user, RoleNames.Cashier));
        });
    }

    [Fact]
    public async Task A_deactivated_employee_cannot_sign_in_or_use_their_refresh_token()
    {
        var (client, tenant) = await OwnerOfAsync("emp-deactivate");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "leaver@emp-deactivate.test", StaffPassword, RoleNames.Cashier, "Leaver");

        // Signed in *before* the deactivation, so there is a live refresh token to kill.
        using var theirs = factory.CreateClient();
        var tokens = await theirs.LoginAsync("emp-deactivate", "leaver@emp-deactivate.test", StaffPassword);

        var deactivated = await client.PostAsJsonAsync($"/api/v1/employees/{staff.Id}/deactivate", new { });
        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        var loggedIn = await theirs.PostAsJsonAsync("/api/v1/auth/login", new
        {
            tenantSlug = "emp-deactivate",
            email = "leaver@emp-deactivate.test",
            password = StaffPassword,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, loggedIn.StatusCode);

        // The long door, closed explicitly by the endpoint. Without the revoke the refresh
        // path would keep working for the token's whole lifetime, which is far longer than
        // the access token's fifteen minutes.
        var refreshed = await theirs.PostAsJsonAsync("/api/v1/auth/refresh", new
        {
            refreshToken = tokens.RefreshToken,
        });

        Assert.Equal(HttpStatusCode.Unauthorized, refreshed.StatusCode);
    }

    [Fact]
    public async Task Deactivating_twice_is_not_an_error()
    {
        var (client, tenant) = await OwnerOfAsync("emp-idempotent-deactivate");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "leaver@emp-idempotent-deactivate.test", StaffPassword, RoleNames.Cashier, "Leaver");

        var first = await client.PostAsJsonAsync($"/api/v1/employees/{staff.Id}/deactivate", new { });
        var second = await client.PostAsJsonAsync($"/api/v1/employees/{staff.Id}/deactivate", new { });

        // A second click on a slow connection asked for a state that already holds. Answering
        // 409 would send an owner looking for a problem that is not there.
        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, second.StatusCode);
    }

    [Fact]
    public async Task A_deactivated_employee_can_be_brought_back_by_editing_them()
    {
        var (client, tenant) = await OwnerOfAsync("emp-reactivate");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "returner@emp-reactivate.test", StaffPassword, RoleNames.Cashier, "Returner");

        await client.PostAsJsonAsync($"/api/v1/employees/{staff.Id}/deactivate", new { });

        var updated = await client.PutAsJsonAsync($"/api/v1/employees/{staff.Id}", new
        {
            displayName = "Returner",
            role = RoleNames.Cashier,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.OK, updated.StatusCode);

        using var theirs = factory.CreateClient();
        var tokens = await theirs.LoginAsync("emp-reactivate", "returner@emp-reactivate.test", StaffPassword);

        Assert.False(string.IsNullOrWhiteSpace(tokens.AccessToken));
    }

    [Theory]
    [InlineData("", "someone@example.com", RoleNames.Cashier, "displayName")]
    [InlineData("Robin", "not-an-email", RoleNames.Cashier, "email")]
    [InlineData("Robin", "someone@example.com", "Superuser", "role")]
    public async Task A_malformed_create_names_the_field(
        string displayName, string email, string role, string expectedField)
    {
        var (client, _) = await OwnerOfAsync($"emp-invalid-{expectedField.ToLowerInvariant()}");

        var created = await client.PostAsJsonAsync("/api/v1/employees", new
        {
            displayName,
            email,
            role,
            password = StaffPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var problem = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty(expectedField, out _));
    }

    [Fact]
    public async Task A_weak_password_is_refused_with_the_rule_that_was_broken()
    {
        var (client, _) = await OwnerOfAsync("emp-weak-password");

        var created = await client.PostAsJsonAsync("/api/v1/employees", new
        {
            displayName = "Robin Vale",
            email = "robin@emp-weak-password.test",
            role = RoleNames.Cashier,
            password = "short",
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var problem = await created.Content.ReadFromJsonAsync<JsonElement>();

        // Identity's own messages, surfaced rather than replaced. "A password is required"
        // when the real problem is the length rule tells an owner to try the same thing again.
        var messages = problem.GetProperty("errors").GetProperty("password");
        Assert.NotEmpty(messages.EnumerateArray());
    }

    [Fact]
    public async Task A_duplicate_email_within_the_tenant_is_refused()
    {
        var (client, tenant) = await OwnerOfAsync("emp-duplicate");

        await factory.CreateUserAsync(
            tenant.Id, "taken@emp-duplicate.test", StaffPassword, RoleNames.Cashier, "First");

        var created = await client.PostAsJsonAsync("/api/v1/employees", new
        {
            displayName = "Second",
            email = "taken@emp-duplicate.test",
            role = RoleNames.Cashier,
            password = StaffPassword,
        });

        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);

        var problem = await created.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(problem.GetProperty("errors").TryGetProperty("email", out _));
    }

    [Fact]
    public async Task The_same_email_is_free_at_a_different_shop()
    {
        var (clientA, _) = await OwnerOfAsync("emp-share-a");
        var (clientB, _) = await OwnerOfAsync("emp-share-b");

        object Staff(string shop) => new
        {
            displayName = "Consultant",
            email = "consultant@example.com",
            role = RoleNames.Manager,
            password = StaffPassword,
            shop,
        };

        var atA = await clientA.PostAsJsonAsync("/api/v1/employees", Staff("a"));
        var atB = await clientB.PostAsJsonAsync("/api/v1/employees", Staff("b"));

        // The uniqueness index leads with the tenant, so one person can hold accounts at two
        // shops — a franchise owner, a consultant, or us doing support. Identity's
        // RequireUniqueEmail is per-tenant here only because the query filter scopes it.
        Assert.Equal(HttpStatusCode.Created, atA.StatusCode);
        Assert.Equal(HttpStatusCode.Created, atB.StatusCode);
    }

    [Fact]
    public async Task Editing_somebody_who_does_not_exist_is_a_404()
    {
        var (client, _) = await OwnerOfAsync("emp-missing");

        var updated = await client.PutAsJsonAsync($"/api/v1/employees/{Guid.NewGuid()}", new
        {
            displayName = "Nobody",
            role = RoleNames.Cashier,
            isActive = true,
        });

        Assert.Equal(HttpStatusCode.NotFound, updated.StatusCode);
    }

    [Fact]
    public async Task Creating_an_employee_is_audited_with_their_role()
    {
        var (client, tenant) = await OwnerOfAsync("emp-audit-create");

        var created = await client.PostAsJsonAsync("/api/v1/employees", new
        {
            displayName = "Robin Vale",
            email = "robin@emp-audit-create.test",
            role = RoleNames.Cashier,
            password = StaffPassword,
        });

        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.EmployeeCreated && a.EntityId == id);

            // ApplicationUser carries no CreatedBy, so without this entry there is no record
            // anywhere of who granted this person access to the till.
            Assert.Equal(nameof(ApplicationUser), entry.EntityType);
            Assert.NotNull(entry.After);
            Assert.Contains(RoleNames.Cashier, entry.After, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Changing_a_role_is_audited_with_both_sides()
    {
        var (client, tenant) = await OwnerOfAsync("emp-audit-role");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "sam@emp-audit-role.test", StaffPassword, RoleNames.Cashier, "Sam Cole");

        await client.PutAsJsonAsync($"/api/v1/employees/{staff.Id}", new
        {
            displayName = "Sam Cole",
            role = RoleNames.Manager,
            isActive = true,
        });

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.RoleChanged && a.EntityId == staff.Id);

            Assert.Contains(RoleNames.Cashier, entry.Before!, StringComparison.Ordinal);
            Assert.Contains(RoleNames.Manager, entry.After!, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task Renaming_somebody_writes_no_role_change()
    {
        var (client, tenant) = await OwnerOfAsync("emp-audit-rename");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "sam@emp-audit-rename.test", StaffPassword, RoleNames.Cashier, "Sam Cole");

        await client.PutAsJsonAsync($"/api/v1/employees/{staff.Id}", new
        {
            displayName = "Samantha Cole",
            role = RoleNames.Cashier,
            isActive = true,
        });

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The audit log is a targeted record of what costs money, not a change-log of
            // every field edit. A typo correction that files a RoleChanged entry is exactly
            // the noise that stops people reading it.
            Assert.False(await db.AuditEntries.AnyAsync(
                a => a.Action == AuditAction.RoleChanged && a.EntityId == staff.Id));
        });
    }

    [Fact]
    public async Task Resetting_a_PIN_is_audited_without_recording_the_PIN()
    {
        var (client, tenant) = await OwnerOfAsync("emp-audit-pin");

        var staff = await factory.CreateUserAsync(
            tenant.Id, "robin@emp-audit-pin.test", StaffPassword, RoleNames.Cashier, "Robin Vale");

        var set = await client.PostAsJsonAsync($"/api/v1/employees/{staff.Id}/set-pin", new { pin = "4821" });
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        await factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var entry = await db.AuditEntries
                .SingleAsync(a => a.Action == AuditAction.PinReset && a.EntityId == staff.Id);

            // No payload at all. The old and new hashes are both secrets, and an audit trail
            // that stores credentials is a credential store with worse access control.
            Assert.Null(entry.Before);
            Assert.Null(entry.After);
        });
    }

    private async Task<(HttpClient Client, Tenant Tenant)> OwnerOfAsync(string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug);
        await factory.CreateUserAsync(tenant.Id, $"owner@{slug}.test", Password, RoleNames.Owner, "Pat Keeper");

        var client = factory.CreateClient();
        var tokens = await client.LoginAsync(slug, $"owner@{slug}.test", Password);
        client.WithBearer(tokens.AccessToken);

        return (client, tenant);
    }
}
