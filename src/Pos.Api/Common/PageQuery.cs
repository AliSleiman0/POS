namespace Pos.Api.Common;

/// <summary>
/// Turns a list endpoint's <c>?limit=</c> and <c>?cursor=</c> into a validated
/// <see cref="PageRequest{TKey}"/>, or into the per-field errors to return.
/// </summary>
/// <remarks>
/// One entry point, so every list endpoint in Phases 2, 6 and 7 rejects a bad page request
/// the same way and with the same wording. The alternative — each handler checking the limit
/// itself — is how one endpoint ends up clamping while another rejects.
/// </remarks>
internal static class PageQuery
{
    /// <summary>Rows returned when the caller does not say.</summary>
    public const int DefaultLimit = 50;

    /// <summary>
    /// The most rows one page will return.
    /// </summary>
    /// <remarks>
    /// A ceiling rather than a suggestion: without one, <c>?limit=1000000</c> is a way for
    /// any authenticated cashier to ask the server to materialise an entire tenant's catalog
    /// into memory and serialise it.
    /// </remarks>
    public const int MaxLimit = 200;

    /// <summary>
    /// Reads the page parameters. Returns <c>false</c> and fills <paramref name="errors"/>
    /// with camelCase field keys ready for <c>TypedResults.ValidationProblem</c>.
    /// </summary>
    /// <remarks>
    /// Both parameters are checked before either can short-circuit, so a request that gets
    /// the limit and the cursor wrong is told about both at once.
    /// </remarks>
    public static bool TryRead<TKey>(
        string? cursor,
        int? limit,
        string sort,
        out PageRequest<TKey> request,
        out Dictionary<string, string[]> errors)
    {
        errors = [];

        // Out of range is refused, not clamped. A client asking for a thousand rows has a
        // bug; silently returning 200 hides it, and the report that quietly stops at 200
        // rows is discovered weeks later as "the numbers don't add up".
        if (limit is { } requested && (requested < 1 || requested > MaxLimit))
        {
            errors["limit"] = [$"A limit between 1 and {MaxLimit} is required."];
        }

        PagePosition<TKey>? after = null;

        // Absent or blank is not an error — it means the first page. A client that clears
        // its cursor to start over should not have to omit the parameter entirely.
        if (!string.IsNullOrWhiteSpace(cursor)
            && !PageCursor.TryDecode(cursor, sort, out after))
        {
            errors["cursor"] = ["The cursor is not valid. Omit it to start from the first page."];
        }

        request = new PageRequest<TKey>(sort, limit ?? DefaultLimit, after);

        return errors.Count == 0;
    }
}
