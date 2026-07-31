using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;
using Testcontainers.PostgreSql;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>
/// The real application, hosted in-process against a real Postgres.
/// </summary>
/// <remarks>
/// Everything this phase promises — query filters, the write interceptor, JWT validation,
/// row-level security — is invisible to a mocked host, and three of the four are invisible
/// to an in-memory database provider. If the suite does not run against the actual
/// pipeline and the actual engine, it is asserting about a system that is not the one
/// being deployed.
/// </remarks>
public sealed class PosApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("pos_test")
        .WithUsername("pos")
        .WithPassword("test_only_not_a_secret")
        .Build();

    /// <summary>
    /// A signing key that exists only for this test run. Long enough to satisfy the
    /// startup validation, which is itself under test.
    /// </summary>
    public const string SigningKey = "test-only-signing-key-that-is-long-enough-32";

    public const string Issuer = "https://pos.test";
    public const string Audience = "https://pos.test";

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        await using var scope = Services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();

        await SeedRolesAsync(scope.ServiceProvider);
    }

    public new async Task DisposeAsync()
    {
        await _container.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // "Testing", not "Development", for one specific reason: Development loads the
        // developer's user secrets, and those contain a connection string pointing at the
        // local Docker database. A test suite that silently ran against it would pass
        // while proving nothing, and would rewrite whatever was in there.
        builder.UseEnvironment("Testing");

        // UseSetting, not ConfigureAppConfiguration. The latter is applied when the host is
        // built, but Program.cs reads builder.Configuration *before* Build() — to fail fast
        // on a missing connection string and signing key. Values added the other way arrive
        // too late to be read, and the app starts with neither.
        builder.UseSetting("ConnectionStrings:Postgres", _container.GetConnectionString());
        builder.UseSetting(JwtOptions.Keys.Issuer, Issuer);
        builder.UseSetting(JwtOptions.Keys.Audience, Audience);
        builder.UseSetting(JwtOptions.Keys.SigningKey, SigningKey);
    }

    /// <summary>Creates a tenant. Not tenant-owned, so no ambient tenant is needed.</summary>
    public async Task<Tenant> CreateTenantAsync(string slug, string name = "Test Shop")
    {
        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = name,
            Slug = slug,
            CurrencyCode = "EUR",
            TimeZoneId = "Europe/Dublin",
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return tenant;
    }

    /// <summary>Creates a user inside <paramref name="tenantId"/> and assigns one role.</summary>
    public async Task<ApplicationUser> CreateUserAsync(
        Guid tenantId,
        string email,
        string password,
        string role,
        string displayName = "Test User")
    {
        await using var scope = Services.CreateAsyncScope();

        // The ordinary path: resolve a tenant, then behave like a single-tenant app.
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        var user = new ApplicationUser
        {
            Id = Guid.CreateVersion7(),
            DisplayName = displayName,
            UserName = email,
            Email = email,
        };

        var created = await users.CreateAsync(user, password);

        if (!created.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not create test user: " + string.Join("; ", created.Errors.Select(e => e.Description)));
        }

        var assigned = await users.AddToRoleAsync(user, role);

        if (!assigned.Succeeded)
        {
            throw new InvalidOperationException(
                "Could not assign role: " + string.Join("; ", assigned.Errors.Select(e => e.Description)));
        }

        return user;
    }

    /// <summary>Runs <paramref name="action"/> in a scope with the tenant resolved.</summary>
    public async Task AsTenantAsync(Guid tenantId, Func<IServiceProvider, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        await action(scope.ServiceProvider);
    }

    private static async Task SeedRolesAsync(IServiceProvider services)
    {
        var roles = services.GetRequiredService<RoleManager<ApplicationRole>>();

        foreach (var role in RoleNames.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new ApplicationRole(role));
            }
        }
    }
}

[CollectionDefinition(Name)]
public sealed class PosApiCollection : ICollectionFixture<PosApiFactory>
{
    public const string Name = "pos-api";
}
