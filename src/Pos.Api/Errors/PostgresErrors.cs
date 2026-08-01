using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Pos.Api.Errors;

/// <summary>
/// Reads the constraint a failed write actually violated, so a handler can turn one
/// specific collision into a domain error and let every other one be a 500.
/// </summary>
/// <remarks>
/// Catching the violation is the whole strategy, not a fallback behind a pre-flight check.
/// "Does this SKU exist yet?" is check-then-act: two concurrent creates both pass it and
/// one still reaches the index. A pre-check therefore cannot remove the case — it can only
/// make it rare enough to show up in production and never in a test — while costing a round
/// trip on every write. The index is the authority; this reads its answer.
/// </remarks>
internal static class PostgresErrors
{
    /// <summary>
    /// Whether <paramref name="exception"/> is a unique violation on exactly
    /// <paramref name="constraintName"/>.
    /// </summary>
    /// <remarks>
    /// The constraint name is not optional. <c>product</c> already carries more than one
    /// unique index and Phase 2.3 adds another; matching on <c>23505</c> alone would report
    /// any of them as whatever the nearest catch block happened to be about. Anything that
    /// does not match falls through and becomes a 500, which is the correct answer for a
    /// constraint nobody wrote a message for.
    /// </remarks>
    /// <summary>
    /// Whether <paramref name="exception"/> is a unique violation on <b>any</b> constraint.
    /// </summary>
    /// <remarks>
    /// The nameless overload exists for exactly one caller: <c>IdempotencyFilter</c>, which is
    /// not asking "which index did I lose on" but "has somebody else already completed this
    /// request?". A concurrent retry can lose on the idempotency index, on a sale's
    /// client-transaction index, or — the case that found this — on the stock row two first
    /// receipts of a new product both tried to create. Enumerating those is a list that goes
    /// stale on the next index; re-reading the key answers the question directly, and the
    /// filter rethrows when the answer is no.
    /// <para>
    /// Everywhere else, name the constraint. Matching on <c>23505</c> alone would report any
    /// of a table's unique indexes as whatever the nearest catch block happened to be about.
    /// </para>
    /// </remarks>
    public static bool IsUniqueViolation(Exception exception) =>
        exception is DbUpdateException
        {
            InnerException: PostgresException { SqlState: PostgresErrorCodes.UniqueViolation },
        };

    public static bool IsUniqueViolation(Exception exception, string constraintName) =>
        exception is DbUpdateException
        {
            InnerException: PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
            } postgres,
        }
        && string.Equals(postgres.ConstraintName, constraintName, StringComparison.Ordinal);
}
