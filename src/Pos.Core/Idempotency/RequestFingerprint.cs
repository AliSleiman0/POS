using System.Security.Cryptography;
using System.Text;

namespace Pos.Core.Idempotency;

/// <summary>
/// A stable digest of "what was asked for", so a replay can be told from a different request
/// wearing the same key.
/// </summary>
/// <remarks>
/// <b>Over the raw request bytes, not a re-serialised DTO.</b> Two reasons, and the second is
/// the one that bites:
/// <list type="number">
/// <item>A client that changes a field the server currently ignores has still changed the
/// request. Hashing the bound object would call that a replay and return the original
/// response, hiding the client's bug at exactly the moment it mattered.</item>
/// <item>Re-serialising makes the hash depend on serializer settings. Turning on camelCase, or
/// changing how a decimal renders, would invalidate every stored key at once — every in-flight
/// retry in every shop would come back 409.</item>
/// </list>
/// <para>
/// The method and path are in the digest, so the same key on a different endpoint is a
/// mismatch rather than a match against whatever happened to be stored.
/// </para>
/// <para>
/// SHA-256 is a BCL primitive, which is what lets this live in Core and be tested without an
/// HTTP request. It is not a security boundary — the key already has to belong to the tenant
/// to be found at all — so it is chosen for being a stable, collision-free-in-practice digest,
/// not for resisting an attacker.
/// </para>
/// </remarks>
public static class RequestFingerprint
{
    /// <summary>
    /// Computes the lowercase hex SHA-256 of <c>method \n path \n body</c>.
    /// </summary>
    /// <param name="method">The HTTP method, e.g. <c>POST</c>.</param>
    /// <param name="path">The request path, e.g. <c>/api/v1/sales</c>.</param>
    /// <param name="body">The raw request body, exactly as it arrived.</param>
    public static string Compute(string method, string path, ReadOnlySpan<byte> body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentNullException.ThrowIfNull(path);

        // The separator is a newline rather than nothing, so that ("POST", "/a/b") and
        // ("POST/a", "/b") cannot produce the same prefix and collide on an empty body.
        var prefix = Encoding.UTF8.GetBytes($"{method}\n{path}\n");

        var buffer = new byte[prefix.Length + body.Length];
        prefix.CopyTo(buffer, 0);
        body.CopyTo(buffer.AsSpan(prefix.Length));

        return Convert.ToHexStringLower(SHA256.HashData(buffer));
    }

    /// <summary>Length of a computed fingerprint: SHA-256 as lowercase hex.</summary>
    public const int Length = 64;
}
