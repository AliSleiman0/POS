using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Pos.Core.Entities;
using Pos.Data;

namespace Pos.Api.Idempotency;

/// <summary>
/// Carries the current request's idempotency key from the filter to whoever owns the
/// transaction, and writes the record into it.
/// </summary>
/// <remarks>
/// <b>Why this is split from the filter at all.</b> The contract in DATA-MODEL.md is that the
/// key is inserted <i>in the same transaction as the work</i>. A filter runs around the
/// handler and cannot be inside a transaction the handler opens, so the filter does the
/// read-validate-replay half and the writer does the insert. Two halves, one contract; a
/// record written after the transaction committed would leave a window in which the work had
/// happened and a retry would do it again.
/// </remarks>
public interface IIdempotencyContext
{
    /// <summary>The key for this request, or null when the endpoint is not idempotent.</summary>
    Guid? Key { get; }

    /// <summary>
    /// Adds the record to <paramref name="db"/> so it commits with the caller's transaction.
    /// </summary>
    /// <remarks>
    /// Added, not saved: the caller's <c>SaveChanges</c> is what writes it, inside the caller's
    /// transaction. A no-op when the endpoint is not idempotent, so a writer shared between a
    /// locked and an unlocked route does not need to ask.
    /// </remarks>
    void Record(AppDbContext db, int status, object? response, DateTimeOffset completedAt);
}

/// <inheritdoc />
internal sealed class IdempotencyContext(IOptions<JsonOptions> jsonOptions) : IIdempotencyContext
{
    public Guid? Key { get; private set; }

    private string? _endpoint;

    private string? _requestHash;

    /// <summary>Called by the filter once it has decided this request is doing the work.</summary>
    public void Begin(Guid key, string endpoint, string requestHash)
    {
        Key = key;
        _endpoint = endpoint;
        _requestHash = requestHash;
    }

    public void Record(AppDbContext db, int status, object? response, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(db);

        if (Key is not { } key)
        {
            return;
        }

        db.IdempotencyRecords.Add(new IdempotencyRecord
        {
            Key = key,
            Endpoint = _endpoint!,
            RequestHash = _requestHash!,
            ResponseStatus = status,

            // Serialised with the host's own options, so the replayed body is what this
            // endpoint would have produced — same casing, same enum-as-name, same null
            // handling. Serialising with defaults here would make a replay differ from the
            // original in ways a client could see.
            ResponseBody = JsonSerializer.Serialize(response, jsonOptions.Value.SerializerOptions),
            CompletedAt = completedAt,
        });
    }
}
