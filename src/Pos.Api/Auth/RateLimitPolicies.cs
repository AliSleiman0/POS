using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Pos.Core.Security;

namespace Pos.Api.Auth;

/// <summary>Rate-limit policy names. Endpoints reference these; nothing spells one by hand.</summary>
public static class RateLimitPolicies
{
    /// <summary>PIN attempts from one till, counted across every member of staff.</summary>
    public const string PinAttempts = "pin-attempts";

    /// <summary>Password attempts from one address, counted across every shop and account.</summary>
    public const string LoginAttempts = "login-attempts";
}

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>Attempts allowed from one till per <see cref="PinAttemptWindow"/>.</summary>
    public const int PinAttemptsPerWindow = 10;

    public static readonly TimeSpan PinAttemptWindow = TimeSpan.FromMinutes(1);

    /// <summary>Password attempts allowed from one address per <see cref="LoginAttemptWindow"/>.</summary>
    /// <remarks>
    /// <b>An entire shop shares one address.</b> Behind NAT — which is every small business
    /// broadband connection — the whole staff signing in at nine o'clock is one partition,
    /// and so is every till, and so is the office. A limit tuned as though one address were
    /// one person locks out a shop on a Monday morning, and the shop's remedy is to
    /// telephone us while unable to trade. That failure is worse than the attack this
    /// stops, because it happens to honest users and it happens reliably.
    /// <para>
    /// Thirty a minute leaves room for that and still caps an attacker at 1,800 an hour
    /// from one source. Combined with Identity's five-attempt account lockout — which this
    /// does not replace — credential stuffing is impractical: the lockout caps guesses per
    /// account, this caps them per source, and an attacker needs both budgets at once.
    /// </para>
    /// </remarks>
    public const int LoginAttemptsPerWindow = 30;

    public static readonly TimeSpan LoginAttemptWindow = TimeSpan.FromMinutes(1);

    // There is deliberately NO application-wide request limiter. See AddPosRateLimiting.

    /// <summary>
    /// Registers the rate-limit policies referenced by <see cref="RateLimitPolicies"/>.
    /// </summary>
    /// <remarks>
    /// The PIN limiter partitions on the <b>device token</b>, not the user, because
    /// per-user lockout alone caps the wrong thing. Identity locks an account after five
    /// failures, so an attacker holding one till simply walks the staff list — five
    /// attempts each, no account ever locked long enough to matter, and
    /// <c>GET /employees/pin-eligible</c> hands over the list to walk. Counting per till
    /// caps total guessing from that till regardless of how many names it tries.
    /// <para>
    /// Both defences are needed and neither replaces the other: the lockout protects one
    /// person's PIN from a distributed attempt, this protects every PIN from one device.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddPosRateLimiting(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(RateLimitPolicies.PinAttempts, PartitionForPinAttempt);
            options.AddPolicy(RateLimitPolicies.LoginAttempts, PartitionForLoginAttempt);

            // NO GlobalLimiter, deliberately.
            //
            // Both limiters here are targeted at guessing a credential, which is a specific
            // attack with a specific shape: unauthenticated, repetitive, and cheap to cap
            // without affecting anybody real. A blanket "N requests a minute from anyone"
            // is a different thing wearing the same clothes, and it is the wrong layer.
            //
            // The number cannot be chosen honestly. It has to sit above whatever the
            // busiest real shop does at its busiest minute — a figure nobody here has
            // measured, because no shop has used this yet — and every estimate that turns
            // out low takes a till down mid-queue, which is the failure this product exists
            // to avoid. A first attempt at 600/minute was low enough that the *test suite*
            // tripped it, which is a fair warning about how good the guess was.
            //
            // Volumetric protection belongs at the edge, where it can be applied without
            // knowing anything about a shop's rhythm: fly.toml sets a concurrency
            // hard_limit per machine, and the platform absorbs a genuine flood before it
            // reaches this process at all. That layer can be tuned from observed traffic
            // once there is some; this one would have to be guessed now and would fail
            // closed on a Saturday.

            options.OnRejected = static (context, cancellationToken) =>
            {
                // Tell the till how long to wait. Without it a queue of staff retries in a
                // tight loop, which keeps the window permanently exhausted for the one
                // cashier who was typing their PIN correctly.
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter =
                        ((int)retryAfter.TotalSeconds).ToString(CultureInfo.InvariantCulture);
                }

                return ValueTask.CompletedTask;
            };
        });

        return services;
    }

    /// <summary>
    /// Password attempts, partitioned on the caller's address.
    /// </summary>
    /// <remarks>
    /// The address and not the email, for the same reason the PIN limiter partitions on the
    /// till and not the user: Identity's per-account lockout already caps guessing against
    /// one account, and an attacker's answer to that is to spread the guesses across
    /// accounts. Nothing stops a script trying one password against every email at a shop —
    /// which is the attack that works, because somebody always has a weak password.
    /// Counting per address caps total guessing from that source however many names it tries.
    /// <para>
    /// Both defences are needed and neither replaces the other: the lockout protects one
    /// person's password against a distributed attempt, this protects every password
    /// against one source.
    /// </para>
    /// <para>
    /// <b>This depends on the address being real.</b> Behind an edge proxy the socket's peer
    /// is the proxy, and every request in the world shares one partition unless
    /// <c>UseForwardedHeaders</c> has run — which is why <c>Hosting:BehindTlsTerminatingProxy</c>
    /// gates both, and why trusting <c>X-Forwarded-For</c> unconditionally would be the
    /// opposite mistake: a spoofable address hands an attacker a fresh budget per request.
    /// </para>
    /// </remarks>
    private static RateLimitPartition<string> PartitionForLoginAttempt(HttpContext httpContext)
    {
        var address = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        return RateLimitPartition.GetFixedWindowLimiter(
            "login:" + address,
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = LoginAttemptsPerWindow,
                Window = LoginAttemptWindow,

                // No queue, same as the PIN limiter: a queued login is somebody watching a
                // spinner, and failing immediately with a Retry-After is the honest answer.
                QueueLimit = 0,
            });
    }

    private static RateLimitPartition<string> PartitionForPinAttempt(HttpContext httpContext)
    {
        var header = httpContext.Request.Headers[DeviceTokenAuthenticationHandler.HeaderName].ToString();

        // Hashed, not raw. The partition key outlives the request inside the limiter's
        // dictionary, and a working device token is a credential — it does not belong in a
        // long-lived in-memory structure, a heap dump or a diagnostic log.
        var key = string.IsNullOrEmpty(header)
            // No token at all: this request is going to 401 at authentication anyway, but it
            // still costs a slot so an unauthenticated flood cannot ride in unmetered.
            ? "anonymous:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown")
            : "device:" + OpaqueToken.Hash(header);

        return RateLimitPartition.GetFixedWindowLimiter(key, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = PinAttemptsPerWindow,
            Window = PinAttemptWindow,

            // No queue. A queued PIN attempt is a cashier standing at a till watching a
            // spinner; failing immediately with a Retry-After is the honest answer.
            QueueLimit = 0,
        });
    }
}
