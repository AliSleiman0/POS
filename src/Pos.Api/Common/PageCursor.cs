using System.Buffers.Text;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Api.Common;

/// <summary>A position in a sort order: the sort key, plus the id that breaks its ties.</summary>
public sealed record PagePosition<TKey>(TKey Key, Guid Id);

/// <summary>A validated <c>?limit=</c> and <c>?cursor=</c>, ready to hand to the query.</summary>
/// <param name="Sort">
/// Which ordering this page belongs to, e.g. <c>product:name</c>. Travels in the cursor so a
/// cursor cannot be replayed against a different one.
/// </param>
/// <param name="Limit">Rows to return. Already range-checked.</param>
/// <param name="After">Where to resume, or <c>null</c> for the first page.</param>
public sealed record PageRequest<TKey>(string Sort, int Limit, PagePosition<TKey>? After);

/// <summary>
/// Encodes and decodes the opaque <c>nextCursor</c> string.
/// </summary>
/// <remarks>
/// <b>A cursor is a position, not a capability.</b> It carries no tenant and grants nothing:
/// tenancy comes from the validated token, and the query filter plus row-level security have
/// already scoped the query before the keyset predicate is applied. A cursor minted in
/// tenant A and replayed in tenant B therefore yields "B's rows sorting after that position",
/// which is a legal page of B's own data and discloses nothing about A. There is a test for
/// exactly that.
/// <para>
/// So it is deliberately <b>not signed</b>. An HMAC protects a capability; adding one here
/// would buy key management and rotation in exchange for tamper-proofing a value whose worst
/// tampered outcome is an empty page. If someone later reaches for signing, this paragraph
/// is the argument they need to rebut.
/// </para>
/// </remarks>
internal static class PageCursor
{
    /// <summary>Bumped if the payload shape ever changes.</summary>
    /// <remarks>
    /// One integer now is what makes a format change later a graceful rejection rather than
    /// "every cursor minted before Tuesday throws". Cursors are short-lived — the client is
    /// mid-scroll — so refusing the old shape costs one re-request.
    /// </remarks>
    private const int CurrentVersion = 1;

    /// <summary>
    /// Refused before decoding. A cursor is ours and is about 80 characters; anything this
    /// long is someone probing, and there is no reason to base64-decode and JSON-parse
    /// several megabytes to tell them so.
    /// </summary>
    private const int MaxEncodedLength = 512;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static string Encode<TKey>(string sort, TKey key, Guid id)
    {
        var payload = new CursorPayload<TKey>(CurrentVersion, sort, key, id);
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, SerializerOptions);

        // Base64Url, not Base64: the value goes in a query string, and '+', '/' and '='
        // would each need escaping on the way out and unescaping on the way back.
        return Base64Url.EncodeToString(json);
    }

    /// <summary>
    /// Reads a cursor, or reports that it cannot be read. Never throws.
    /// </summary>
    /// <remarks>
    /// Every rejection is the same answer to the caller, on purpose: distinguishing "bad
    /// base64" from "wrong sort order" would tell someone probing which part of their guess
    /// was closer, and tells a legitimate client nothing it can act on — the fix for all of
    /// them is to drop the cursor and start again.
    /// </remarks>
    public static bool TryDecode<TKey>(string cursor, string expectedSort, out PagePosition<TKey>? position)
    {
        position = null;

        if (cursor.Length > MaxEncodedLength)
        {
            return false;
        }

        byte[] json;

        try
        {
            json = Base64Url.DecodeFromChars(cursor);
        }
        catch (FormatException)
        {
            return false;
        }

        CursorPayload<TKey>? payload;

        try
        {
            payload = JsonSerializer.Deserialize<CursorPayload<TKey>>(json, SerializerOptions);
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            // The JSON parsed but `k` is not a TKey — a string key against an endpoint whose
            // key is a Guid, say. Reachable only by a hand-made cursor.
            return false;
        }

        if (payload is null || payload.V != CurrentVersion || payload.K is null)
        {
            return false;
        }

        // The sort token is what stops a /tax-classes cursor being applied to /products.
        // Without it the keyset predicate would compare that endpoint's key column against
        // this one's value, which is a cast failure at the database and a 500 at the client.
        if (!string.Equals(payload.S, expectedSort, StringComparison.Ordinal))
        {
            return false;
        }

        position = new PagePosition<TKey>(payload.K, payload.I);

        return true;
    }

    /// <summary>
    /// Short property names because this is base64'd into a URL on every page, and
    /// <c>"version"</c> costs six characters more than <c>"v"</c> every time.
    /// </summary>
    private sealed record CursorPayload<TKey>(
        [property: JsonPropertyName("v")] int V,
        [property: JsonPropertyName("s")] string S,
        [property: JsonPropertyName("k")] TKey K,
        [property: JsonPropertyName("i")] Guid I);
}
