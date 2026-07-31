using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.Core.Entities;
using Pos.Data.Identity;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Identity;

/// <summary>
/// Identity, made tenant-aware without Identity knowing tenants exist.
/// </summary>
/// <remarks>
/// The mechanism is the ordinary query filter: Identity's uniqueness checks and lookups
/// are queries through <c>AppDbContext</c>, so they are scoped for free. What needed
/// changing was the two platform-wide unique indexes it declares.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantScopedIdentityTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_same_person_can_hold_an_account_at_two_tenants()
    {
        // The franchise owner, the consultant, and us doing support. Identity's stock
        // schema makes this a duplicate-key error during onboarding, which reads like a
        // bug in the onboarding code rather than a constraint anybody chose.
        const string SharedEmail = "ann@example.com";

        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        var first = await CreateUserAsync(tenantA, SharedEmail);
        var second = await CreateUserAsync(tenantB, SharedEmail);

        Assert.True(first.Succeeded, Describe(first));
        Assert.True(second.Succeeded, Describe(second));
    }

    [Fact]
    public async Task The_same_email_twice_within_one_tenant_is_rejected()
    {
        var tenantId = await CreateTenantAsync();
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        var first = await CreateUserAsync(tenantId, email);
        var second = await CreateUserAsync(tenantId, email);

        Assert.True(first.Succeeded, Describe(first));
        Assert.False(second.Succeeded);
    }

    [Fact]
    public async Task Uniqueness_within_a_tenant_is_enforced_by_the_database_not_only_by_Identity()
    {
        var tenantId = await CreateTenantAsync();
        var email = $"race-{Guid.NewGuid():N}@example.com";

        var created = await CreateUserAsync(tenantId, email);
        Assert.True(created.Succeeded, Describe(created));

        // Bypasses UserManager entirely — the shape of two concurrent registrations, where
        // both pass Identity's "is this taken?" query before either insert lands. A query
        // loses that race; a unique index does not.
        await using var scoped = ScopedDbContext.ForTenant(postgres.ConnectionString, tenantId);

        scoped.Db.Users.Add(new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            DisplayName = "Racer",
            Email = email,
            NormalizedEmail = email.ToUpperInvariant(),
            UserName = $"other-{Guid.NewGuid():N}@example.com",
            NormalizedUserName = $"OTHER-{Guid.NewGuid():N}@EXAMPLE.COM",
        });

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => scoped.Db.SaveChangesAsync());
        var postgresError = Assert.IsType<PostgresException>(ex.InnerException);
        Assert.Equal(PostgresErrorCodes.UniqueViolation, postgresError.SqlState);
    }

    [Fact]
    public async Task A_user_lookup_in_one_tenant_cannot_find_another_tenants_user()
    {
        const string SharedEmail = "shared-lookup@example.com";

        var tenantA = await CreateTenantAsync();
        var tenantB = await CreateTenantAsync();

        var created = await CreateUserAsync(tenantA, SharedEmail, displayName: "Ann at A");
        Assert.True(created.Succeeded, Describe(created));

        await using var asTenantB = ScopedDbContext.ForTenant(postgres.ConnectionString, tenantB);
        var users = asTenantB.Services.GetRequiredService<UserManager<ApplicationUser>>();

        // FindByEmailAsync is a query through AppDbContext, so the global filter applies.
        // This is the property that lets login name the tenant first and then behave
        // exactly like a single-tenant application everywhere after.
        Assert.Null(await users.FindByEmailAsync(SharedEmail));

        await using var asTenantA = ScopedDbContext.ForTenant(postgres.ConnectionString, tenantA);
        var found = await asTenantA.Services.GetRequiredService<UserManager<ApplicationUser>>()
            .FindByEmailAsync(SharedEmail);

        Assert.NotNull(found);
        Assert.Equal("Ann at A", found.DisplayName);
    }

    [Fact]
    public async Task A_user_is_stamped_with_the_ambient_tenant_without_being_told()
    {
        var tenantId = await CreateTenantAsync();
        var email = $"stamp-{Guid.NewGuid():N}@example.com";

        await using var scoped = ScopedDbContext.ForTenant(postgres.ConnectionString, tenantId);
        var users = scoped.Services.GetRequiredService<UserManager<ApplicationUser>>();

        // No TenantId set by the caller: ApplicationUser implements ITenantOwned, so the
        // interceptor stamps it like any other tenant-owned row.
        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            DisplayName = "Unstamped",
            UserName = email,
            Email = email,
        };

        var result = await users.CreateAsync(user, "Correct-Horse-9");
        Assert.True(result.Succeeded, Describe(result));
        Assert.Equal(tenantId, user.TenantId);
    }

    [Fact]
    public async Task Roles_are_platform_wide_and_carry_no_tenant()
    {
        // A role row is the string "Manager". There is no tenant data in it to leak, and
        // three fixed strings per tenant would be rows for their own sake. What a role may
        // *do* is the policy map in the API, not a column here.
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var entityType = scoped.Db.Model.FindEntityType(typeof(ApplicationRole))!;

        Assert.Null(entityType.FindProperty("TenantId"));
        Assert.Empty(entityType.GetDeclaredQueryFilters());
    }

    private async Task<Guid> CreateTenantAsync()
    {
        var tenantId = Guid.CreateVersion7();

        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        scoped.Db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Test Tenant",
            Slug = $"t-{tenantId:N}"[..20],
            CurrencyCode = "EUR",
            TimeZoneId = "Europe/Dublin",
        });

        await scoped.Db.SaveChangesAsync();

        return tenantId;
    }

    private async Task<IdentityResult> CreateUserAsync(Guid tenantId, string email, string displayName = "Test User")
    {
        await using var scoped = ScopedDbContext.ForTenant(postgres.ConnectionString, tenantId);
        var users = scoped.Services.GetRequiredService<UserManager<ApplicationUser>>();

        return await users.CreateAsync(
            new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                DisplayName = displayName,
                UserName = email,
                Email = email,
            },
            "Correct-Horse-9");
    }

    private static string Describe(IdentityResult result)
        => string.Join("; ", result.Errors.Select(e => $"{e.Code}: {e.Description}"));
}
