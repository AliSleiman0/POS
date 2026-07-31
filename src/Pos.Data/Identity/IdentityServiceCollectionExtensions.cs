using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Pos.Data.Identity;

public static class IdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers ASP.NET Core Identity against <see cref="AppDbContext"/>.
    /// </summary>
    /// <remarks>
    /// <c>AddIdentityCore</c>, not <c>AddIdentity</c>: the latter wires up cookie
    /// authentication and an external-login scheme, neither of which exists here. Tokens
    /// are JWTs issued by this API — see DECISIONS.md, "Auth".
    /// <para>
    /// Identity's own uniqueness checks run as queries through <see cref="AppDbContext"/>,
    /// so the tenant query filter makes them per-tenant without Identity knowing tenants
    /// exist. "This email is taken" now means taken <i>at this shop</i>.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPosIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;

                // Length over character-class gymnastics: a required symbol mostly buys
                // "Password1!" written on a sticky note. The 4-6 digit cashier PIN is a
                // separate secret with its own defences (device token + lockout) and is
                // not governed by these rules.
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false;
                options.Password.RequireDigit = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireLowercase = true;

                // Lockout is load-bearing for PIN login, not a nicety: four digits is
                // 10,000 possibilities, so without a cap the PIN is decoration.
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<AppDbContext>();

        // No AddDefaultTokenProviders(): those back password-reset and 2FA flows, neither
        // of which exists yet. Adding them when the first one does keeps the surface
        // honest — an unused token provider is still a token provider.
        return services;
    }
}
