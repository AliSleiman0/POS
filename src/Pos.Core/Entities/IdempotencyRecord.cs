using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// The stored answer to a money- or stock-moving request, so a retry replays instead of
/// repeating.
/// </summary>
/// <remarks>
/// <b>This and <c>Sale.ClientTransactionId</c> both exist, and they guarantee different
/// things.</b> The unique index on the sale is the <i>domain</i> guarantee — "exactly one sale
/// for this cart" stays true even if this table were dropped, and it is what Phase 9's outbox
/// reconciles against when it reads sales back. This row is the <i>transport</i> guarantee: it
/// stores the original status and body so a replay is byte-identical, and it covers the write
/// paths that create no sale at all — a stock adjustment, opening or closing a shift, a cash
/// movement — plus a void, which mutates a sale rather than inserting one.
/// <para>
/// Written inside the same transaction as the work it describes. That is the whole contract:
/// if the sale commits, the key commits with it, and if either fails neither is there. A
/// record written afterwards would leave a window in which the work had happened and a retry
/// would do it again.
/// </para>
/// </remarks>
public sealed class IdempotencyRecord : TenantEntity
{
    public const int EndpointMaxLength = 200;

    /// <summary>
    /// The client's GUID, from the <c>Idempotency-Key</c> header. Unique per tenant.
    /// </summary>
    /// <remarks>
    /// The uniqueness deliberately does <b>not</b> include <see cref="Endpoint"/>. Reusing one
    /// key on two different endpoints is the same client bug as reusing it with two different
    /// bodies, and it deserves the same 409 rather than quietly succeeding twice. The endpoint
    /// is part of <see cref="RequestHash"/> instead, which is what turns it into a mismatch.
    /// </remarks>
    public Guid Key { get; set; }

    /// <summary>Method and path, for diagnosing a mismatch a client cannot explain.</summary>
    public required string Endpoint { get; set; }

    /// <summary>
    /// Lowercase hex SHA-256 of the request, from <c>RequestFingerprint</c>.
    /// </summary>
    public required string RequestHash { get; set; }

    /// <summary>The status the original attempt answered with.</summary>
    public int ResponseStatus { get; set; }

    /// <summary>
    /// The original response body, verbatim.
    /// </summary>
    /// <remarks>
    /// <c>text</c> and not <c>jsonb</c>, deliberately. Postgres reorders object keys and drops
    /// duplicates when it parses jsonb, so a replayed body would not be byte-identical to the
    /// one the client was originally given — and "the same response" is the entire promise.
    /// </remarks>
    public required string ResponseBody { get; set; }

    /// <summary>When the original attempt completed, server-set from <c>TimeProvider</c>.</summary>
    public DateTimeOffset CompletedAt { get; set; }
}
