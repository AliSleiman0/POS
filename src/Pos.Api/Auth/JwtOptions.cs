using System.ComponentModel.DataAnnotations;
using System.Text;

namespace Pos.Api.Auth;

/// <summary>
/// Access-token signing configuration. Bound from <c>Jwt:*</c>.
/// </summary>
/// <remarks>
/// Validated with <c>ValidateOnStart</c>, so a deployment with no signing key fails at
/// boot with a clear message instead of starting happily and signing tokens with an empty
/// key — which every instance would then accept from anybody.
/// </remarks>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>Configuration keys, used by the test host to supply values early.</summary>
    public static class Keys
    {
        public const string Issuer = "Jwt:Issuer";
        public const string Audience = "Jwt:Audience";
        public const string SigningKey = "Jwt:SigningKey";
    }

    /// <summary>
    /// HMAC-SHA256 needs at least 256 bits of key to be worth the name.
    /// </summary>
    public const int MinimumSigningKeyBytes = 32;

    [Required(AllowEmptyStrings = false)]
    public string Issuer { get; set; } = string.Empty;

    [Required(AllowEmptyStrings = false)]
    public string Audience { get; set; } = string.Empty;

    /// <summary>
    /// From user-secrets locally, environment or vault in production. Never a literal in
    /// source and never in appsettings.json.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>
    /// Short, because an access token cannot be revoked — the only thing limiting a stolen
    /// one is its expiry. The refresh token carries the long-lived session and *can* be
    /// revoked.
    /// </summary>
    [Range(1, 60)]
    public int AccessTokenMinutes { get; set; } = 15;

    /// <summary>How long a refresh-token family lives before the user must log in again.</summary>
    [Range(1, 365)]
    public int RefreshTokenDays { get; set; } = 30;

    /// <summary>Rejects a key that is present but too short to be a real key.</summary>
    public bool HasUsableSigningKey()
        => !string.IsNullOrWhiteSpace(SigningKey)
           && Encoding.UTF8.GetByteCount(SigningKey) >= MinimumSigningKeyBytes;
}
