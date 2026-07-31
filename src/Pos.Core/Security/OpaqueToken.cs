using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace Pos.Core.Security;

/// <summary>
/// The format shared by every token this system hands out that is <b>not</b> a JWT:
/// refresh tokens and register device tokens.
/// </summary>
/// <remarks>
/// <code>&lt;base64url(tenantId)&gt;.&lt;base64url(32 random bytes)&gt;</code>
/// <para>
/// The tenant prefix exists because these tokens are presented <i>before</i> any tenant is
/// known — a refresh token arrives with no access token, and a device token arrives from a
/// till that has never logged in. Without the prefix, looking one up would mean querying
/// the token table across every tenant, which is exactly the unscoped read this phase
/// exists to make impossible.
/// </para>
/// <para>
/// The prefix is <b>not</b> a credential and is not trusted for anything but narrowing:
/// it selects which tenant to search. Authentication is the 256-bit random half, matched
/// by hash inside that tenant. A forged prefix simply lands the caller in a tenant where
/// their hash matches nothing.
/// </para>
/// <para>
/// Hashing is a plain SHA-256, deliberately unlike the PIN and password hashes. Those
/// protect low-entropy secrets a human chose and need to be slow; these are 256 random
/// bits, where slowness buys nothing and a deterministic digest is required to look the
/// row up at all.
/// </para>
/// </remarks>
public static class OpaqueToken
{
    /// <summary>Bytes of randomness in the secret half. 256 bits — not guessable, not stretched.</summary>
    public const int SecretByteCount = 32;

    private const char Separator = '.';

    /// <summary>Mints a new token for <paramref name="tenantId"/>.</summary>
    public static string Issue(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A token must name a real tenant.", nameof(tenantId));
        }

        var secret = RandomNumberGenerator.GetBytes(SecretByteCount);

        return string.Concat(
            Base64Url.EncodeToString(tenantId.ToByteArray()),
            Separator,
            Base64Url.EncodeToString(secret));
    }

    /// <summary>
    /// Reads the tenant a token claims to belong to, without trusting it for anything else.
    /// </summary>
    public static bool TryReadTenant(string? token, out Guid tenantId)
    {
        tenantId = Guid.Empty;

        if (string.IsNullOrEmpty(token))
        {
            return false;
        }

        var separator = token.IndexOf(Separator, StringComparison.Ordinal);

        // Both halves must be present. A token with no secret is not a short token, it is
        // an attempt to be admitted on the strength of a public tenant id.
        if (separator <= 0 || separator == token.Length - 1)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[16];

        if (!Base64Url.TryDecodeFromChars(token.AsSpan(0, separator), buffer, out var written) || written != 16)
        {
            return false;
        }

        tenantId = new Guid(buffer);
        return tenantId != Guid.Empty;
    }

    /// <summary>The value stored in the database. Never store the token itself.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return Convert.ToBase64String(digest);
    }
}
