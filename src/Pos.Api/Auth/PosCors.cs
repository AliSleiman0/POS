using System.Globalization;
using Microsoft.Extensions.Options;
using Pos.Api.Idempotency;

namespace Pos.Api.Auth;

/// <summary>
/// The one CORS policy this API has, and the startup validation that keeps it honest.
/// </summary>
public static class PosCors
{
    /// <summary>The policy name. Nothing spells this by hand.</summary>
    public const string PolicyName = "pos-web";

    /// <summary>
    /// Response headers a browser may let script read.
    /// </summary>
    /// <remarks>
    /// Cross-origin, script sees only the CORS-safelisted response headers unless the
    /// server names the rest here. Both of these are headers this API sets deliberately
    /// and would otherwise set into a void.
    /// </remarks>
    public static readonly string[] ExposedHeaders =
    [
        // "That sale was already recorded" is a different thing to tell a cashier than
        // "sale recorded", and after a timeout and a retry it is the true one. Read by
        // wasReplayed() in the web app's api/idempotency.ts — the header is the only
        // evidence of a replay, so losing it cross-origin would make a duplicate-looking
        // charge indistinguishable from a fresh one at the till.
        IdempotencyFilter.ReplayHeaderName,

        // Set by RateLimitPolicies.OnRejected so a till knows how long to wait rather than
        // retrying in a tight loop and keeping its own window permanently exhausted. That
        // is advice the client cannot take if it cannot read it.
        "Retry-After",
    ];

    /// <summary>
    /// Request headers this API actually reads.
    /// </summary>
    /// <remarks>
    /// Listed rather than allowed wholesale, so adding one is a deliberate act with a
    /// reason — and cross-checked by <c>CorsPolicyTests</c> against every
    /// <c>HeaderName</c> constant in the assembly, because the failure mode for a missing
    /// entry is narrow and nasty. <c>X-Override-Authorization</c> is the example: leave it
    /// out and every ordinary sale still works, while a manager's approval of a discount
    /// or a price override fails preflight — the one path a shop uses when a customer is
    /// already standing there arguing about a price.
    /// </remarks>
    public static readonly string[] AllowedHeaders =
    [
        "Authorization",
        "Content-Type",
        IdempotencyFilter.HeaderName,
        DeviceTokenAuthenticationHandler.HeaderName,
        OverrideGrantService.HeaderName,
    ];

    /// <summary>Registers the policy from <c>Cors:AllowedOrigins</c>.</summary>
    public static IServiceCollection AddPosCors(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<CorsOptions>()
            .Bind(configuration.GetSection(CorsOptions.SectionName))
            .ValidateDataAnnotations();

        var origins = Validate(configuration
            .GetSection(CorsOptions.Keys.AllowedOrigins)
            .Get<string[]>() ?? []);

        services.AddCors(options => options.AddPolicy(PolicyName, policy =>
        {
            if (origins.Length == 0)
            {
                // No origin is allowed, which is what an empty list means. Said out loud
                // rather than by falling through, because a policy built with no origins
                // and no other call still adds the middleware and it is worth being able
                // to read that this is the intended shape.
                return;
            }

            policy
                .WithOrigins(origins)
                .WithHeaders(AllowedHeaders)
                .WithExposedHeaders(ExposedHeaders)
                .WithMethods("GET", "POST", "PUT", "DELETE");

            // Deliberately NO AllowCredentials. Tokens travel in the Authorization header
            // from sessionStorage, never in a cookie (DECISIONS.md, Phase 4.2 and 8.2), so
            // nothing here needs credentialed CORS — and turning it on is what would make
            // a future cookie ambient on cross-site requests and require a CSRF story.
        }));

        return services;
    }

    /// <summary>
    /// Refuses to start on a configuration that cannot mean what it says.
    /// </summary>
    /// <remarks>
    /// Same shape as <c>JwtOptions.ValidateOnStart</c>, and for the same reason: a CORS
    /// mistake is invisible from the server. The API answers every probe healthily while
    /// the browser blocks every call, and the only evidence is a console message on
    /// somebody else's machine.
    /// </remarks>
    private static string[] Validate(string[] origins)
    {
        foreach (var origin in origins)
        {
            if (string.Equals(origin, "*", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Cors:AllowedOrigins may not contain '*'. This API is reachable with a till's " +
                    "own session; any origin means any page a cashier has open can read the shop's " +
                    "catalog, sales and staff list. List exact origins.");
            }

            if (!Uri.TryCreate(origin, UriKind.Absolute, out var parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException(
                    $"Cors:AllowedOrigins contains '{origin}', which is not an absolute http or " +
                    "https origin. Expected something like https://pos.fly.dev");
            }

            // A browser's Origin header is scheme://host[:port] and nothing else, so an
            // entry carrying a path or a trailing slash matches no request that will ever
            // arrive. It looks correct in configuration and blocks everything.
            if (parsed.AbsolutePath != "/" || origin.EndsWith('/'))
            {
                throw new InvalidOperationException(
                    $"Cors:AllowedOrigins contains '{origin}', which has a path or a trailing " +
                    "slash. An Origin header is scheme://host[:port] only, so this would match " +
                    $"nothing. Use '{parsed.GetLeftPart(UriPartial.Authority)}'.");
            }
        }

        return origins;
    }

    /// <summary>
    /// Fails the boot if Production has no allowed origin.
    /// </summary>
    /// <remarks>
    /// Called separately from <see cref="AddPosCors"/> because it is a statement about the
    /// deployment rather than about the value: an empty list is correct in development,
    /// where Vite proxies the API onto the page's own origin and there is no cross-origin
    /// caller at all.
    /// <para>
    /// In Production it means the web app cannot reach the API — every request blocked,
    /// the API reporting healthy throughout. The one legitimate case is an API deployed
    /// with no browser client (the Avalonia desktop app of Phase 12 sends no Origin and
    /// CORS does not apply to it); that would be a deliberate change to this method, which
    /// is the point of it being loud.
    /// </para>
    /// </remarks>
    public static void ValidateForEnvironment(IServiceProvider services, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        if (!environment.IsProduction())
        {
            return;
        }

        var options = services.GetRequiredService<IOptions<CorsOptions>>().Value;

        if (options.AllowedOrigins.Count == 0)
        {
            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} is empty in Production, so no browser may call this API and the web app " +
                "cannot work. Set it to the web app's exact origin — for example " +
                "`fly secrets set {0}__0=https://pos.fly.dev`.",
                CorsOptions.Keys.AllowedOrigins.Replace(':', '_')));
        }
    }
}
