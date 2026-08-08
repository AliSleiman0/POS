using System.Globalization;
using Microsoft.IdentityModel.JsonWebTokens;
using Pos.Api.Auth;
using Pos.Core.Auditing;

namespace Pos.Api.Tenancy;

/// <summary>
/// The authenticated user, for the <c>CreatedBy</c>/<c>UpdatedBy</c> stamps and the audit log.
/// </summary>
/// <remarks>
/// Null on an anonymous request. That is correct rather than a gap: rows written during
/// login are written by the system, and inventing an actor for them would put a
/// misleading name on an audit trail people are meant to trust.
/// </remarks>
public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public Guid? UserId => ClaimAsGuid(JwtRegisteredClaimNames.Sub);

    /// <remarks>
    /// Present on PIN sessions (<c>TokenService</c> adds it when the login came from an
    /// enrolled till) and on device-token requests. An owner signing in with a password from
    /// a browser has no register, and gets null.
    /// </remarks>
    public Guid? RegisterId => ClaimAsGuid(PosClaims.RegisterId);

    private Guid? ClaimAsGuid(string claimType)
    {
        var value = accessor.HttpContext?.User.FindFirst(claimType)?.Value;

        return Guid.TryParse(value, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
