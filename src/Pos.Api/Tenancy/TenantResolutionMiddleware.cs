using System.Globalization;
using Pos.Api.Auth;
using Pos.Core.Tenancy;

namespace Pos.Api.Tenancy;

/// <summary>
/// Populates the ambient tenant from the <c>tenant_id</c> claim of a <b>validated</b> token.
/// </summary>
/// <remarks>
/// Runs after authentication, so by the time this reads the claim the signature has
/// already been checked. That ordering is the entire security argument: a claim from an
/// unvalidated token is just a string the caller typed.
/// <para>
/// An authenticated request with no usable <c>tenant_id</c> is rejected outright. There is
/// no sensible default — "unknown tenant" must never quietly become "some tenant".
/// </para>
/// </remarks>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, AmbientTenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(tenantContext);

        if (context.User.Identity?.IsAuthenticated == true)
        {
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
        }

        await next(context);
    }
}
