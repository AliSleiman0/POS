using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Core.Entities;
using Pos.Core.Security;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;
using Pos.TestSupport;
using Testcontainers.PostgreSql;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>A till and the device token it was enrolled with.</summary>
public sealed record EnrolledRegister(Guid Id, string DeviceToken);

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

    /// <summary>
    /// What the hosted application connects as: <c>pos_app</c>, which is
    /// <c>NOBYPASSRLS</c>. Set before the host is built, since that is when
    /// <see cref="ConfigureWebHost"/> reads it.
    /// </summary>
    private string _appConnectionString = string.Empty;

    private readonly SemaphoreSlim _worldGate = new(1, 1);
    private Isolation.TwoTenantWorld? _world;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        var ownerConnectionString = _container.GetConnectionString();

        await AppRoleBootstrap.CreateAsync(ownerConnectionString);
        _appConnectionString = AppRoleBootstrap.ConnectionStringFor(ownerConnectionString);

        // Migrations run as the owner, in a service provider of their own. They cannot run
        // through the hosted app's provider any more: pos_app has no CREATE on the schema,
        // which is the point of it. Splitting the two roles here is what makes the suite
        // exercise the same privilege boundary a real deployment has.
        var migrator = new ServiceCollection();
        migrator.AddPosData(ownerConnectionString);

        await using (var provider = migrator.BuildServiceProvider())
        {
            await using var migrationScope = provider.CreateAsyncScope();
            await migrationScope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        }

        await using var scope = Services.CreateAsyncScope();
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
        // pos_app, not the container's owner account. The owner is a superuser and
        // bypasses row-level security unconditionally, so a suite run as the owner asserts
        // that isolation works while proving only that the query filters do.
        builder.UseSetting("ConnectionStrings:Postgres", _appConnectionString);
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

    /// <summary>
    /// Creates a till and enrolls it, returning the device token the real endpoint would
    /// have shown once.
    /// </summary>
    /// <remarks>
    /// Enrollment is exercised through HTTP by its own test. This shortcut exists so that
    /// every *other* device test starts from an enrolled till without first logging in as
    /// an Owner, which would make a PIN test fail for reasons that have nothing to do with
    /// PINs.
    /// </remarks>
    public async Task<EnrolledRegister> CreateEnrolledRegisterAsync(Guid tenantId, string name = "Till 1")
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var register = new Register { Name = name };
        db.Registers.Add(register);
        await db.SaveChangesAsync();

        var token = OpaqueToken.Issue(tenantId);
        register.DeviceTokenHash = OpaqueToken.Hash(token);
        await db.SaveChangesAsync();

        return new EnrolledRegister(register.Id, token);
    }

    /// <summary>Creates a till and leaves it unenrolled, as <c>POST /registers</c> does.</summary>
    public async Task<Guid> CreateRegisterAsync(Guid tenantId, string name)
    {
        var id = Guid.Empty;

        await AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var register = new Register { Name = name };

            db.Registers.Add(register);
            await db.SaveChangesAsync();

            id = register.Id;
        });

        return id;
    }

    /// <summary>
    /// The two deliberately identical tenants the isolation suite attacks across, seeded
    /// once for the whole assembly.
    /// </summary>
    /// <remarks>
    /// Seeding costs several Identity password hashes per tenant, which are slow by design,
    /// so this is built on first use and shared. It is safe to share only because no test
    /// adds to or removes from the collections the suite asserts on — see the note on
    /// <see cref="Isolation.TwoTenantWorld"/>.
    /// </remarks>
    public async Task<Isolation.TwoTenantWorld> IsolationWorldAsync()
    {
        if (_world is not null)
        {
            return _world;
        }

        await _worldGate.WaitAsync();

        try
        {
            return _world ??= await Isolation.TwoTenantWorld.SeedAsync(this);
        }
        finally
        {
            _worldGate.Release();
        }
    }

    /// <summary>Clears a till's device token, as <c>POST /registers/{id}/revoke</c> does.</summary>
    public Task RevokeRegisterAsync(Guid tenantId, Guid registerId) =>
        AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var register = await db.Registers.FirstAsync(r => r.Id == registerId);

            register.DeviceTokenHash = null;
            await db.SaveChangesAsync();
        });

    /// <summary>Sets a cashier's PIN using the same hasher the endpoint uses.</summary>
    public Task SetPinAsync(Guid tenantId, Guid userId, string pin) =>
        AsTenantAsync(tenantId, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var user = await users.Users.FirstAsync(u => u.Id == userId);

            user.PinHash = users.PasswordHasher.HashPassword(user, pin);
            await users.UpdateAsync(user);
        });

    /// <summary>
    /// A user's failed-attempt counter — the evidence for whether a request reached the PIN
    /// check or was turned away before it.
    /// </summary>
    public async Task<int> AccessFailedCountAsync(Guid tenantId, Guid userId)
    {
        var count = 0;

        await AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            count = (await db.Users.FirstAsync(u => u.Id == userId)).AccessFailedCount;
        });

        return count;
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
