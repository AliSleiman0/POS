using System.Globalization;
using Microsoft.IdentityModel.JsonWebTokens;
using Pos.Api.Auth;
using Pos.Core.Tenancy;

namespace Pos.Api.Tenancy;

/// <summary>
/// Populates the ambient tenant from the <c>tenant_id</c> claim of a <b>validated</b> token,
/// and opens the logging scope every line for this request is written inside.
/// </summary>
/// <remarks>
/// Runs after authentication, so by the time this reads the claim the signature has
/// already been checked. That ordering is the entire security argument: a claim from an
/// unvalidated token is just a string the caller typed.
/// <para>
/// An authenticated request with no usable <c>tenant_id</c> is rejected outright. There is
/// no sensible default — "unknown tenant" must never quietly become "some tenant".
/// </para>
/// <para>
/// The logging scope lives here rather than in a middleware of its own because this is the
/// one component that knows the tenant, and a scope opened before it would have to guess.
/// Without <c>TenantId</c> on the line, "tenant X reports a wrong total" is uninvestigable:
/// the logs of every shop are interleaved in one stream and nothing says which is which.
/// </para>
/// </remarks>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>
    /// Scope keys. Named as constants because a log query is written against these strings
    /// and a rename that only changes the producer silently empties a dashboard.
    /// </summary>
    public static class ScopeKeys
    {
        public const string TenantId = "TenantId";
        public const string UserId = "UserId";
        public const string RegisterId = "RegisterId";
    }

    public async Task InvokeAsync(
        HttpContext context,
        AmbientTenantContext tenantContext,
        ILogger<TenantResolutionMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(logger);

        if (context.User.Identity?.IsAuthenticated != true)
        {
            // Anonymous — login, refresh, a health probe. There is no tenant to name, and
            // inventing one would be worse than the absence. ASP.NET Core's own hosting
            // scope still carries RequestId and RequestPath, so the line is traceable.
            await next(context);
            return;
        }

        var claim = context.User.FindFirst(PosClaims.TenantId)?.Value;

        if (!Guid.TryParse(claim, CultureInfo.InvariantCulture, out var tenantId) || tenantId == Guid.Empty)
        {
            // A token we signed, carrying no tenant. Either the issuing path has a bug
            // or the token predates a change; both are reasons to stop, not continue.
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Login and refresh resolve the tenant before issuing a token, so on those
        // paths this is a no-op re-assertion of the same value. Resolve() allows that
        // and rejects a genuine mismatch.
        tenantContext.Resolve(tenantId);

        // Ids only. Never the display name, the email or anything else about the person:
        // a log line goes to an aggregator, a backup and eventually a support screenshot,
        // and an id can be resolved to a person by someone with the right to do so while a
        // name cannot be taken back out again.
        var scope = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [ScopeKeys.TenantId] = tenantId,
        };

        if (context.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value is { Length: > 0 } userId)
        {
            scope[ScopeKeys.UserId] = userId;
        }

        // Present only for a session started by PIN at an enrolled till. Worth having:
        // "which register was this rung on" is the first question asked about a
        // discrepancy, and answering it from the drawer count alone means guessing.
        if (context.User.FindFirst(PosClaims.RegisterId)?.Value is { Length: > 0 } registerId)
        {
            scope[ScopeKeys.RegisterId] = registerId;
        }

        using (logger.BeginScope(scope))
        {
            await next(context);
        }
    }
}
