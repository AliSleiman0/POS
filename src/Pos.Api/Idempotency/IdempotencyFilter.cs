using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Errors;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Idempotency;
using Pos.Data;

namespace Pos.Api.Idempotency;

/// <summary>
/// Reads <c>Idempotency-Key</c>, replays a known request, and refuses a reused key.
/// </summary>
/// <remarks>
/// The read-validate-replay half of the contract; the insert is
/// <see cref="IIdempotencyContext.Record"/>, because it has to land inside the transaction
/// that does the work. See <see cref="IIdempotencyContext"/>.
/// <para>
/// <b>The request body has to be buffered for this to work at all.</b> Minimal-API endpoint
/// filters run <i>after</i> model binding, so by the time this executes the body stream has
/// already been read to the end. Without the rewind below — and the
/// <c>EnableBuffering()</c> in <c>Program.cs</c> that makes rewinding possible — every
/// fingerprint would be computed over zero bytes, every key would look like a match, and the
/// system would replay a stored response for a request that had nothing in common with it.
/// That failure is silent and it is the reason the differing-body test was written first.
/// </para>
/// </remarks>
internal sealed class IdempotencyFilter : IEndpointFilter
{
    /// <summary>The header clients send. Not <c>X-</c> prefixed: RFC 6648 deprecated that.</summary>
    public const string HeaderName = "Idempotency-Key";

    /// <summary>Set on a replayed response so a client can tell one from a fresh success.</summary>
    public const string ReplayHeaderName = "Idempotent-Replay";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;

        if (!http.Request.Headers.TryGetValue(HeaderName, out var values)
            || !Guid.TryParse(values.ToString(), out var key)
            || key == Guid.Empty)
        {
            // 400 with a field error, not 428 Precondition Required. A missing or malformed
            // required header is a malformed request, and every other malformed request in
            // this API is answered with a ValidationProblem naming what was wrong — a client
            // that has to branch on 428 for this one case is a client that will not.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                [HeaderName] =
                [
                    "A unique GUID is required, generated before the first attempt and reused "
                    + "on every retry of it.",
                ],
            });
        }

        var db = http.RequestServices.GetRequiredService<AppDbContext>();
        var idempotency = http.RequestServices.GetRequiredService<IdempotencyContext>();

        var endpoint = $"{http.Request.Method} {http.Request.Path}";
        var hash = RequestFingerprint.Compute(
            http.Request.Method,
            http.Request.Path,
            await ReadBodyAsync(http));

        var existing = await db.IdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Key == key, http.RequestAborted);

        if (existing is not null)
        {
            return Replay(http, existing, key, hash);
        }

        idempotency.Begin(key, endpoint, hash);

        try
        {
            return await next(context);
        }
        catch (DbUpdateException exception) when (PostgresErrors.IsUniqueViolation(exception))
        {
            // The concurrent case, and the reason a unique index is the mechanism rather than
            // the lookup above. Two retries both missed that read and both did the work; this
            // one lost a race to insert something.
            //
            // The question deliberately is not "which index did I lose on". A concurrent retry
            // can collide on the idempotency key, on a sale's client_transaction_id, or on the
            // stock row that two first-receipts of a brand-new product both tried to create —
            // and enumerating those is a list that goes stale on the next index. Asking
            // whether the key is now present answers it directly.
            //
            // A single re-read is enough, with no retry loop, because of how it lost: the
            // INSERT blocked until the winner's transaction committed, so by the time the
            // 23505 arrived the winner's row was already visible. This attempt's own
            // transaction has rolled back, so nothing of its work survives.
            var winner = await db.IdempotencyRecords
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Key == key, http.RequestAborted);

            if (winner is null)
            {
                // Lost a race to something that was not this request. Inventing a response
                // would be worse than saying so.
                throw;
            }

            return Replay(http, winner, key, hash);
        }
    }

    /// <summary>
    /// Returns the stored response, or refuses the key if it was issued for something else.
    /// </summary>
    private static ContentHttpResult Replay(HttpContext http, IdempotencyRecord record, Guid key, string hash)
    {
        if (!string.Equals(record.RequestHash, hash, StringComparison.Ordinal))
        {
            // Surfaced rather than absorbed. Answering with the stored response would show the
            // till a sale that succeeded — for a basket the customer never had.
            throw new IdempotencyKeyReusedException(
                $"Key {key} was already used for {record.Endpoint}. Generate a new key for a new "
                + "request, and reuse one only when retrying the request it was made for.");
        }

        http.Response.Headers[ReplayHeaderName] = "true";

        // The stored body verbatim, at the stored status. Not re-derived, because "the same
        // response" has to include whatever the original computed — the change due, the sale
        // number, everything the client already acted on.
        //
        // Headers other than this marker are NOT reproduced: a replayed 201 carries no
        // Location. The client following a retry already has the first response's Location,
        // and storing arbitrary headers to replay one would be a column for a value nothing
        // reads. Recorded here rather than discovered.
        return TypedResults.Text(
            record.ResponseBody,
            contentType: "application/json",
            statusCode: record.ResponseStatus);
    }

    /// <summary>
    /// Rewinds and reads the buffered request body.
    /// </summary>
    /// <remarks>
    /// The raw bytes, never a re-serialisation of the bound DTO: a client that changed a field
    /// this server currently ignores has still changed the request, and re-serialising would
    /// also make every stored hash depend on serializer settings.
    /// </remarks>
    private static async Task<byte[]> ReadBodyAsync(HttpContext http)
    {
        if (!http.Request.Body.CanSeek)
        {
            // The buffering middleware did not run for this route. Hashing zero bytes here
            // would make every key look like a match, so refuse to guess.
            throw new InvalidOperationException(
                "The request body is not buffered, so an idempotency fingerprint cannot be "
                + "computed. Register the body-buffering middleware before routing.");
        }

        http.Request.Body.Position = 0;

        using var buffer = new MemoryStream();
        await http.Request.Body.CopyToAsync(buffer, http.RequestAborted);

        // Left rewound: nothing downstream reads it again today, but leaving a consumed stream
        // behind is the kind of action-at-a-distance that costs an afternoon later.
        http.Request.Body.Position = 0;

        return buffer.ToArray();
    }
}
