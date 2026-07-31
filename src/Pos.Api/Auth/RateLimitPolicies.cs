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
}

public static class RateLimitingServiceCollectionExtensions
{
    /// <summary>Attempts allowed from one till per <see cref="PinAttemptWindow"/>.</summary>
    public const int PinAttemptsPerWindow = 10;

    public static readonly TimeSpan PinAttemptWindow = TimeSpan.FromMinutes(1);

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
