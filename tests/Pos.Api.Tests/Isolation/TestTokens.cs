using System.Security.Claims;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// Mints access tokens directly, so the suite can present ones <c>TokenService</c> would
/// never issue.
/// </summary>
/// <remarks>
/// This duplicates the claim building in <c>TokenService.CreateAccessToken</c> on purpose.
/// Calling the real service would only ever produce well-formed tokens, and the question
/// here is what the server does with a malformed one — a token with no tenant, with a
/// tenant that is not a guid, or signed by somebody else.
/// </remarks>
internal static class TestTokens
{
    public static string Signed(
        IEnumerable<Claim> claims,
        string? signingKey = null,
        string? issuer = null,
        string? audience = null)
    {
        var key = new SymmetricSecurityKey(
            Encoding.UTF8.GetBytes(signingKey ?? PosApiFactory.SigningKey));

        var now = DateTime.UtcNow;

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer ?? PosApiFactory.Issuer,
            Audience = audience ?? PosApiFactory.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(15),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    /// <summary>Reads the tenant a token carries, without validating it.</summary>
    public static string? TenantOf(string accessToken) =>
        new JsonWebTokenHandler().ReadJsonWebToken(accessToken).GetClaim(PosClaims.TenantId)?.Value;
}
