using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One issued refresh token, stored hashed and rotated on every use.
/// </summary>
/// <remarks>
/// Rotation alone does not detect theft — a stolen token used once still works. What
/// detects it is the <see cref="FamilyId"/>: every token descended from one login shares
/// it, and presenting a token that has already been rotated means two parties hold tokens
/// from the same chain. That can only happen if one of them copied it, so the whole family
/// is revoked and both are forced to log in again.
/// <para>
/// The legitimate user hitting this sees one unexpected logout. The alternative is a thief
/// keeping a valid session indefinitely by refreshing it, which is the failure this design
/// exists to prevent.
/// </para>
/// </remarks>
public sealed class RefreshToken : TenantEntity
{
    /// <summary>Length of the stored SHA-256 digest in base64.</summary>
    public const int TokenHashLength = 44;

    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the full token string. The token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Shared by every token descended from one login. The unit of revocation.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>
    /// The register this session was started on, for a PIN login. Carried on the family so
    /// a refresh keeps the register claim — a till whose session silently stops being
    /// register-bound after fifteen minutes would be a confusing kind of broken.
    /// </summary>
    public Guid? RegisterId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>Set when this token is rotated. Its presence means the token has been spent.</summary>
    public Guid? ReplacedByTokenId { get; set; }

    /// <summary>Whether this token can still be exchanged.</summary>
    public bool IsActive(DateTimeOffset now)
        => RevokedAt is null && ReplacedByTokenId is null && ExpiresAt > now;
}
