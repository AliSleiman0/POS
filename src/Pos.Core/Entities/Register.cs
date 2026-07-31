using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A physical till, enrolled once and thereafter identified by a device token.
/// </summary>
/// <remarks>
/// The register is what makes PIN login defensible. A 4-digit PIN is 10,000
/// possibilities — trivially exhausted by anything that can talk to the API. Requiring a
/// 256-bit device token first means an attacker must already hold a specific, revocable
/// piece of hardware before the PIN is even consulted, which turns the PIN back into what
/// it should be: proof of <i>which member of staff</i> is at a till we already trust.
/// </remarks>
public sealed class Register : TenantEntity
{
    public const int NameMaxLength = 80;

    /// <summary>Length of the stored SHA-256 digest in base64.</summary>
    public const int DeviceTokenHashLength = 44;

    public required string Name { get; set; }

    /// <summary>
    /// SHA-256 of the device token, or null when the register has never been enrolled or
    /// has been revoked. Never the token itself — enrollment shows it once and then it is
    /// unrecoverable, so a database dump does not hand over working tills.
    /// </summary>
    public string? DeviceTokenHash { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>Last successful PIN login from this device. Answers "is that lost tablet still in use?"</summary>
    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Whether this register may currently authenticate a device.</summary>
    public bool IsEnrolled => IsActive && !string.IsNullOrEmpty(DeviceTokenHash);
}
