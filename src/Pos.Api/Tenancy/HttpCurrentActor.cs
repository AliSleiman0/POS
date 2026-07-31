using System.Globalization;
using Microsoft.IdentityModel.JsonWebTokens;
using Pos.Core.Auditing;

namespace Pos.Api.Tenancy;

/// <summary>
/// The authenticated user, for the <c>CreatedBy</c>/<c>UpdatedBy</c> stamps.
/// </summary>
/// <remarks>
/// Null on an anonymous request. That is correct rather than a gap: rows written during
/// login are written by the system, and inventing an actor for them would put a
/// misleading name on an audit trail people are meant to trust.
/// </remarks>
public sealed class HttpCurrentActor(IHttpContextAccessor accessor) : ICurrentActor
{
    public Guid? UserId
    {
        get
        {
            var subject = accessor.HttpContext?.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

            return Guid.TryParse(subject, CultureInfo.InvariantCulture, out var id) ? id : null;
        }
    }
}
