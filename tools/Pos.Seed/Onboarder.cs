using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Core.Entities;
using Pos.Core.Security;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Seed;

/// <summary>Everything the operator needs from a successful onboarding, once.</summary>
internal sealed record OnboardResult(
    Guid TenantId,
    string Slug,
    string OwnerEmail,
    Guid OwnerId,
    string Password,
    bool PasswordWasGenerated,
    Guid? RegisterId,
    string? DeviceToken);

/// <summary>
/// Creates a tenant and its first Owner, once, for a real shop.
/// </summary>
/// <remarks>
/// Writes through <c>AddPosData</c> and <c>UserManager</c> exactly as <see cref="DevSeeder"/>
/// does — the same interceptors, the same query filters, the same password hasher the login
/// endpoint verifies against. A SQL script would produce rows that look right and cannot be
/// logged into.
/// <para>
/// <b>Not idempotent, on purpose.</b> The dev seeder is safe to re-run because it is run
/// repeatedly against a database somebody is testing against. Onboarding the same shop twice
/// is a mistake — either a typo in a slug or a second attempt at something that already
/// worked — and continuing would attach a second Owner to a live tenant.
/// </para>
/// </remarks>
internal sealed class Onboarder(IServiceProvider services, OnboardOptions options)
{
    public async Task<OnboardResult> RunAsync()
    {
        await EnsureSchemaIsCurrentAsync();
        await EnsureRolesAsync();

        var tenant = await CreateTenantAsync();
        var owner = await CreateOwnerAsync(tenant.Id);

        (Guid Id, string Token)? register = options.WithRegister
            ? await CreateRegisterAsync(tenant.Id)
            : null;

        return new OnboardResult(
            tenant.Id,
            tenant.Slug,
            options.OwnerEmail,
            owner,
            options.OwnerPassword,
            options.PasswordWasGenerated,
            register?.Id,
            register?.Token);
    }

    /// <summary>
    /// Refuses to write into a schema that is behind its migrations.
    /// </summary>
    /// <remarks>
    /// Onboarding into a stale schema half-works: the tables that exist take their rows and
    /// the failure arrives partway through as a missing column, leaving a tenant with no
    /// Owner and a slug that this command will now refuse to reuse.
    /// </remarks>
    private async Task EnsureSchemaIsCurrentAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        string[] pending;

        try
        {
            pending = [.. await db.Database.GetPendingMigrationsAsync()];
        }
        catch (NpgsqlException ex)
        {
            throw new SeedException(
                "Could not reach the database with the connection string given." +
                Environment.NewLine + "Npgsql said: " + ex.Message,
                ex);
        }

        if (pending.Length > 0)
        {
            throw new SeedException(
                $"The database is {pending.Length.ToString(CultureInfo.InvariantCulture)} migration(s) " +
                $"behind (first: {pending[0]}). Migrations are a gated deploy step run as the schema " +
                "owner — see .github/workflows/deploy.yml. Deploy first, then onboard.");
        }
    }

    /// <summary>
    /// Creates the three role rows if this is the first tenant on a fresh database.
    /// </summary>
    /// <remarks>
    /// Not tenant-owned — one set for the platform — so this is a no-op for every tenant
    /// after the first.
    /// </remarks>
    private async Task EnsureRolesAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<ApplicationRole>>();

        foreach (var role in RoleNames.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                Check(await roles.CreateAsync(new ApplicationRole(role)), $"create the '{role}' role");
            }
        }
    }

    private async Task<Tenant> CreateTenantAsync()
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // No ambient tenant, and none needed: `tenant` is the tenant list, so it carries no
        // TenantId, no query filter and no RLS policy.
        if (await db.Tenants.AnyAsync(t => t.Slug == options.Slug))
        {
            throw new SeedException(
                $"A tenant with the slug '{options.Slug}' already exists. Onboarding is not " +
                "idempotent: continuing would attach a second Owner to a live shop. If this is " +
                "a retry, check whether the first attempt succeeded before running anything else.");
        }

        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(now),
            Name = options.TenantName,
            Slug = options.Slug,
            CurrencyCode = options.CurrencyCode,
            TimeZoneId = options.TimeZoneId,

            // The two that cannot be changed afterwards, which is why the command requires
            // them rather than defaulting them.
            TaxMode = options.TaxMode,
            BusinessDayStartOffset = options.BusinessDayStartOffset,

            // Left at zero unless a shop asks. Rounding is reachable through PUT /settings,
            // and guessing it wrong makes every cash total slightly wrong in a way that
            // reads as a pricing bug.
            CashRoundingIncrement = 0m,

            AddressLine = options.AddressLine,
            TaxNumber = options.TaxNumber,
            ReceiptHeader = options.ReceiptHeader,
            ReceiptFooter = options.ReceiptFooter,

            // Stamped by hand: Tenant is not a TenantEntity, so the interceptor does not see it.
            CreatedAt = now,
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return tenant;
    }

    private async Task<Guid> CreateOwnerAsync(Guid tenantId)
    {
        await using var scope = services.CreateAsyncScope();

        // The ordinary application path: resolve a tenant, then behave like a single-tenant
        // app. Everything below this line is stamped and scoped by the interceptors.
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            UserName = options.OwnerEmail,
            Email = options.OwnerEmail,
            DisplayName = options.OwnerName,
        };

        Check(await users.CreateAsync(user, options.OwnerPassword), $"create {options.OwnerEmail}");
        Check(await users.AddToRoleAsync(user, RoleNames.Owner), "give them the Owner role");

        // No PIN. An Owner signs in with an email and a password; a PIN is for a cashier at
        // an enrolled till, and setting one here would create a way into the shop's
        // most-privileged account that is four digits long.
        return user.Id;
    }

    private async Task<(Guid Id, string Token)> CreateRegisterAsync(Guid tenantId)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var register = new Register { Name = "Front Counter" };
        db.Registers.Add(register);
        await db.SaveChangesAsync();

        // Shown once and never again — only the SHA-256 is stored, so a database dump does
        // not hand over working tills.
        var token = OpaqueToken.Issue(tenantId);

        register.DeviceTokenHash = OpaqueToken.Hash(token);
        await db.SaveChangesAsync();

        return (register.Id, token);
    }

    private static void Check(IdentityResult result, string attempted)
    {
        if (!result.Succeeded)
        {
            throw new SeedException(
                $"Could not {attempted}: " +
                string.Join("; ", result.Errors.Select(error => error.Description)));
        }
    }
}
