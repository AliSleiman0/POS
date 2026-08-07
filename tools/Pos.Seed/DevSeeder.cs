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

/// <summary>A user this run either created or found already present.</summary>
internal sealed record SeededUser(string Role, string Email, string DisplayName, Guid Id, string? Pin, bool Created);

/// <summary>A till, and the device token if this run is the one that enrolled it.</summary>
internal sealed record SeededRegister(string Name, Guid Id, string? DeviceToken, bool Enrolled);

/// <summary>
/// Creates a tenant with staff and tills to log into. Development only.
/// </summary>
/// <remarks>
/// There is no onboarding endpoint by decision (DECISIONS.md, "Platform admin"), so a
/// freshly migrated database has zero tenants and zero users and nothing can be exercised
/// by hand. This fills that gap without adding a provisioning endpoint that would then
/// exist in production.
/// <para>
/// It writes through <c>AddPosData</c> and <c>UserManager</c> rather than SQL, so every
/// row it creates goes through the tenant interceptors and the same password hasher the
/// login endpoint verifies against. A SQL script would produce rows that look right and
/// cannot be logged into.
/// </para>
/// <para>
/// <b>Re-running is safe.</b> Anything already present is left exactly as it is — this is
/// run repeatedly against a database that already has hand-made test data in it, and a
/// seeder that overwrote would destroy the thing being tested.
/// </para>
/// </remarks>
internal sealed class DevSeeder(IServiceProvider services, SeedOptions options)
{
    public async Task RunAsync()
    {
        await EnsureSchemaIsCurrentAsync();
        await EnsureRolesAsync();

        var (tenant, tenantCreated) = await EnsureTenantAsync();

        // Display names deliberately avoid the words Owner/Manager/Cashier. A test proving
        // that a response leaks no role name is defeated by fixture data containing one,
        // which has already happened once here (see docs/HANDOFF.md).
        var owner = await EnsureUserAsync(tenant.Id, RoleNames.Owner, "owner", "Ada Byrne", pin: null);
        var manager = await EnsureUserAsync(tenant.Id, RoleNames.Manager, "manager", "Sam Cole", options.ManagerPin);
        var cashier = await EnsureUserAsync(tenant.Id, RoleNames.Cashier, "cashier", "Robin Vale", options.CashierPin);

        // One enrolled and one not: PIN login needs an enrolled till, and the enrollment
        // endpoint needs something left to enroll.
        var front = await EnsureRegisterAsync(tenant.Id, "Front Counter", enroll: true);
        var back = await EnsureRegisterAsync(tenant.Id, "Back Counter", enroll: false);

        // Last, because it is the only part that depends on a schema newer than Phase 1 —
        // if the catalog tables are missing, everything above has already been written.
        var catalog = options.SkipCatalog
            ? CatalogSummary.Skipped
            : await new CatalogSeeder(services).RunAsync(tenant.Id);

        Report(tenant, tenantCreated, [owner, manager, cashier], [front, back], catalog);
    }

    /// <summary>
    /// Refuses to seed a schema that is behind its migrations.
    /// </summary>
    /// <remarks>
    /// Seeding first would half-work: the tables that exist take their rows, and the
    /// failure arrives as a missing-column error partway through, leaving a tenant with one
    /// user in it. Migrations are a deliberate step run as the schema owner (CLAUDE.md), so
    /// this tool states the command rather than running it — <c>pos_app</c> could not run it
    /// anyway.
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
                "Could not reach the database. Is it running? `docker compose up -d`" +
                Environment.NewLine +
                "Npgsql said: " + ex.Message,
                ex);
        }

        if (pending.Length == 0)
        {
            return;
        }

        throw new SeedException(
            $"The database is {pending.Length.ToString(CultureInfo.InvariantCulture)} migration(s) behind " +
            $"(first: {pending[0]}). Migrations connect as the schema owner, not pos_app — run:" +
            Environment.NewLine + Environment.NewLine +
            "  dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api \\" +
            Environment.NewLine +
            "    --connection \"Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret\"");
    }

    /// <summary>Creates the three role rows. Not tenant-owned — one set for the platform.</summary>
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

    private async Task<(Tenant Tenant, bool Created)> EnsureTenantAsync()
    {
        if (options.CurrencyCode.Length != 3)
        {
            throw new SeedException("--currency must be a 3-letter ISO 4217 code, e.g. EUR.");
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // No ambient tenant, and none needed: `tenant` is the tenant list, so it carries no
        // TenantId, no query filter and no RLS policy. See Tenant's own remarks.
        var existing = await db.Tenants.FirstOrDefaultAsync(t => t.Slug == options.Slug);

        if (existing is not null)
        {
            // The two fields a re-run may change, and only when they were asked for. There is
            // no PUT /settings, so this is the only way to give an existing dev shop a rounding
            // increment — and without one the register's rounding line cannot be reached from
            // a browser at all. The footer is here for the same reason: it is the receipt field
            // most worth seeing change.
            var changed = false;

            if (options.CashRoundingIncrement is { } increment
                && existing.CashRoundingIncrement != increment)
            {
                existing.CashRoundingIncrement = increment;
                changed = true;
            }

            if (options.ReceiptFooter is { } footer && existing.ReceiptFooter != footer)
            {
                existing.ReceiptFooter = footer;
                changed = true;
            }

            if (changed)
            {
                await db.SaveChangesAsync();
            }

            return (existing, false);
        }

        var now = scope.ServiceProvider.GetRequiredService<TimeProvider>().GetUtcNow();

        var tenant = new Tenant
        {
            Id = Guid.CreateVersion7(now),
            Name = options.TenantName,
            Slug = options.Slug,
            CurrencyCode = options.CurrencyCode,
            TimeZoneId = options.TimeZoneId,
            CashRoundingIncrement = options.CashRoundingIncrement ?? 0m,

            // The receipt header block. Filled in rather than left null so a seeded shop
            // prints a realistic receipt — a dev tenant with no address and no tax number
            // exercises none of the lines a real one is legally required to show.
            AddressLine = SeedOptions.DefaultAddressLine,
            TaxNumber = SeedOptions.DefaultTaxNumber,
            ReceiptHeader = SeedOptions.DefaultReceiptHeader,
            ReceiptFooter = options.ReceiptFooter ?? SeedOptions.DefaultReceiptFooter,

            // Stamped by hand because Tenant is not a TenantEntity, so the audit
            // interceptor does not see it.
            CreatedAt = now,
        };

        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();

        return (tenant, true);
    }

    private async Task<SeededUser> EnsureUserAsync(
        Guid tenantId,
        string role,
        string localPart,
        string displayName,
        string? pin)
    {
        await using var scope = services.CreateAsyncScope();

        // The ordinary application path: resolve a tenant, then behave like a single-tenant
        // app. Everything written below this line is stamped and scoped by the interceptors.
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var email = $"{localPart}@{options.Slug}.test";

        // Tenant-filtered, so this finds a user at this shop and no other — the same email
        // at a second seeded tenant is a different person.
        var user = await users.FindByEmailAsync(email);
        var created = false;

        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = email,
                Email = email,
                DisplayName = displayName,
            };

            Check(await users.CreateAsync(user, options.Password), $"create {email}");
            created = true;
        }

        if (!await users.IsInRoleAsync(user, role))
        {
            Check(await users.AddToRoleAsync(user, role), $"give {email} the '{role}' role");
        }

        // Only when there is no PIN yet. Overwriting one would silently change a PIN
        // somebody set by hand, and the summary below would then print a PIN that is not
        // the one in the database.
        var pinSet = pin is not null && user.PinHash is null;

        if (pinSet)
        {
            user.PinHash = users.PasswordHasher.HashPassword(user, pin!);
            Check(await users.UpdateAsync(user), $"set the PIN for {email}");
        }

        return new SeededUser(role, email, displayName, user.Id, pinSet ? pin : null, created);
    }

    private async Task<SeededRegister> EnsureRegisterAsync(Guid tenantId, string name, bool enroll)
    {
        await using var scope = services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<AmbientTenantContext>().Resolve(tenantId);

        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var register = await db.Registers.FirstOrDefaultAsync(r => r.Name == name);

        if (register is null)
        {
            register = new Register { Name = name };
            db.Registers.Add(register);
            await db.SaveChangesAsync();
        }

        if (!enroll || (register.DeviceTokenHash is not null && !options.RotateDeviceToken))
        {
            return new SeededRegister(name, register.Id, DeviceToken: null, register.DeviceTokenHash is not null);
        }

        // The token is shown here and never again — only its SHA-256 is stored, so a
        // database dump does not hand over working tills.
        var token = OpaqueToken.Issue(tenantId);

        register.DeviceTokenHash = OpaqueToken.Hash(token);
        await db.SaveChangesAsync();

        return new SeededRegister(name, register.Id, token, Enrolled: true);
    }

    private void Report(
        Tenant tenant,
        bool tenantCreated,
        IReadOnlyList<SeededUser> users,
        IReadOnlyList<SeededRegister> registers,
        CatalogSummary catalog)
    {
        var writer = Console.Out;

        writer.WriteLine();
        writer.WriteLine($"Tenant  {tenant.Slug}  ({tenant.Name})  {State(tenantCreated)}");
        writer.WriteLine($"        id {Format(tenant.Id)}");
        writer.WriteLine();

        foreach (var user in users)
        {
            writer.WriteLine($"  {user.Role,-8} {user.Email,-34} {user.DisplayName,-12} {State(user.Created)}");
            writer.WriteLine($"           id {Format(user.Id)}{PinNote(user)}");
        }

        writer.WriteLine();

        foreach (var register in registers)
        {
            var status = register.Enrolled ? "enrolled" : "not enrolled";
            writer.WriteLine($"  {register.Name,-15} {status,-13} id {Format(register.Id)}");

            if (register.DeviceToken is not null)
            {
                writer.WriteLine($"                  X-Device-Token: {register.DeviceToken}");
                writer.WriteLine("                  Shown once. Re-run with --rotate-device-token to reissue.");
            }
        }

        if (catalog != CatalogSummary.Skipped)
        {
            writer.WriteLine();
            writer.WriteLine(
                $"  Catalog        {catalog.Products} products, {catalog.Barcodes} barcodes, " +
                $"{catalog.StockItems} stock rows, {catalog.Categories} categories, " +
                $"{catalog.TaxClasses} tax classes");
            writer.WriteLine("                  SKU-1001 has two barcodes; SKU-1003 is sold by the kilogram;");
            writer.WriteLine("                  SKU-1005 costs 0.1650; SKU-1006 has no category and no stock.");
        }

        writer.WriteLine();
        writer.WriteLine($"Password for users created by this run: {options.Password}");
        writer.WriteLine("Existing users keep whatever password they already had.");
        writer.WriteLine();
        writer.WriteLine("Log in (the API listens on 5013 — see launchSettings.json):");
        writer.WriteLine();
        writer.WriteLine("  POST http://localhost:5013/api/v1/auth/login");
        writer.WriteLine(
            $"  {{ \"tenantSlug\": \"{tenant.Slug}\", \"email\": \"{users[0].Email}\", " +
            $"\"password\": \"{options.Password}\" }}");
        writer.WriteLine();
        writer.WriteLine("PIN login, from an enrolled till (needs the X-Device-Token header above):");
        writer.WriteLine();
        writer.WriteLine("  POST http://localhost:5013/api/v1/auth/pin");
        writer.WriteLine($"  {{ \"userId\": \"{Format(users[^1].Id)}\", \"pin\": \"{options.CashierPin}\" }}");

        if (catalog != CatalogSummary.Skipped)
        {
            writer.WriteLine();
            writer.WriteLine("To look at what was written, browse the API:");
            writer.WriteLine();
            writer.WriteLine("  dotnet run --project src/Pos.Api      then open http://localhost:5013/scalar/");
            writer.WriteLine();
            writer.WriteLine("Sign in through POST /api/v1/auth/login with the slug and credentials above, then");
            writer.WriteLine("GET /api/v1/products. As the owner you will see costPrice; as the cashier you");
            writer.WriteLine("will not, because the field is omitted from the payload rather than hidden.");
            writer.WriteLine();
            writer.WriteLine("Or read the rows directly, which is still the only way to see stock levels until");
            writer.WriteLine("Phase 2.4 adds the ledger endpoints:");
            writer.WriteLine();
            writer.WriteLine("  docker exec -e PGPASSWORD=dev_only_not_a_secret pos-db psql -U pos -d pos_dev -c \"");
            writer.WriteLine("    SELECT p.sku, p.name, p.unit, p.unit_price, t.rate, s.on_hand");
            writer.WriteLine("    FROM product p");
            writer.WriteLine("    JOIN tax_class t ON (t.tenant_id, t.id) = (p.tenant_id, p.tax_class_id)");
            writer.WriteLine("    LEFT JOIN stock_item s ON (s.tenant_id, s.product_id) = (p.tenant_id, p.id)");
            writer.WriteLine($"    WHERE p.tenant_id = '{Format(tenant.Id)}' ORDER BY p.sku;\"");
            writer.WriteLine();
            writer.WriteLine("That connects as the schema owner, which bypasses row-level security and sees");
            writer.WriteLine("every tenant — not how the application ever connects.");
        }

        writer.WriteLine();
    }

    private static string PinNote(SeededUser user) => user.Pin is not null
        ? $"   PIN {user.Pin}"
        : string.Empty;

    private static string State(bool created) => created ? "created" : "already present";

    /// <summary>Culture-independent by construction, and explicit about it.</summary>
    private static string Format(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns an Identity failure into a message naming what was being attempted.
    /// </summary>
    /// <remarks>
    /// Identity returns results rather than throwing, so an unchecked call fails silently
    /// and the seeder reports success over a user that does not exist.
    /// </remarks>
    private static void Check(IdentityResult result, string attempted)
    {
        if (result.Succeeded)
        {
            return;
        }

        throw new SeedException(
            $"Could not {attempted}: " + string.Join("; ", result.Errors.Select(e => e.Description)));
    }
}
