using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pos.Core.Security;
using Pos.Core.Tenancy;
using Pos.Data;

namespace Pos.Api.Auth;

/// <summary>
/// Authenticates an enrolled register from its <c>X-Device-Token</c> header.
/// </summary>
/// <remarks>
/// A full authentication scheme rather than a check inside the endpoint, for one reason
/// that matters: authentication runs <b>before</b> the endpoint does. A PIN request from
/// an unenrolled device is therefore rejected before the PIN is read, let alone verified —
/// so an attacker without a device token cannot use the endpoint as a PIN oracle at all.
/// </remarks>
public sealed class DeviceTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db,
    AmbientTenantContext tenantContext)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "DeviceToken";
    public const string HeaderName = "X-Device-Token";

    /// <summary>Policy name for endpoints reachable only from an enrolled till.</summary>
    public const string PolicyName = "EnrolledDevice";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var header))
        {
            // No result rather than a failure: the caller simply did not attempt this
            // scheme, and saying so would let a probe distinguish "wrong token" from "no
            // token" for free.
            return AuthenticateResult.NoResult();
        }

        var presented = header.ToString();

        if (!OpaqueToken.TryReadTenant(presented, out var tenantId))
        {
            return AuthenticateResult.Fail("Malformed device token.");
        }

        // A request can carry both an access token and a device token, and the JWT will
        // already have resolved the tenant by now. Two different tenants in one request is
        // not a scope we can serve — and AmbientTenantContext refuses to switch, so without
        // this it surfaces as a 500 rather than the rejection it is.
        if (tenantContext.IsResolved && tenantContext.TenantId != tenantId)
        {
            return AuthenticateResult.Fail("Device token belongs to a different tenant.");
        }

        // The prefix chooses which tenant to search; the hash below is what authenticates.
        tenantContext.Resolve(tenantId);

        var hash = OpaqueToken.Hash(presented);

        var register = await db.Registers
            .FirstOrDefaultAsync(r => r.DeviceTokenHash == hash && r.IsActive);

        if (register is null)
        {
            // Covers a revoked till (hash cleared), a deactivated one, and a forged prefix
            // that landed in a tenant where this hash exists nowhere.
            return AuthenticateResult.Fail("Unknown or revoked device.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(PosClaims.TenantId, register.TenantId.ToString()),
                new Claim(PosClaims.RegisterId, register.Id.ToString()),
            ],
            SchemeName);

        return AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
