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
