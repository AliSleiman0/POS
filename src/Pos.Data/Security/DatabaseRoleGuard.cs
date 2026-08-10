using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace Pos.Data.Security;

/// <summary>
/// Refuses to run against a database connection that row-level security cannot bind.
/// </summary>
/// <remarks>
/// Phase 1.6 built RLS as the layer that holds when application code is wrong: the query
/// filters and the write interceptor are both application-side, and neither applies to raw
/// SQL. The whole of that depends on one fact about the connection — that the login role is
/// <c>NOBYPASSRLS</c>.
/// <para>
/// <b>Connecting as the owner disables every policy while leaving them visibly "enabled".</b>
/// <c>pg_policies</c> still lists them, <c>\d</c> still shows "row security enabled", the
/// application still works, and every tenant can read every other tenant's sales. There is
/// no error and no log line; the only way to know is to ask. Managed Postgres hands you a
/// superuser by default, so the mistake is one copied connection string away and would be
/// found by a customer rather than by us.
/// </para>
/// <para>
/// This is deliberately a boot-time refusal rather than a documented manual check. A check
/// performed once during Phase 8.2 tells you nothing about the connection string in place
/// six months later.
/// </para>
/// </remarks>
public static class DatabaseRoleGuard
{
    /// <summary>
    /// Reads the login role and its <c>BYPASSRLS</c> attribute, and throws unless the role
    /// is subject to policy.
    /// </summary>
    /// <remarks>
    /// <paramref name="attempts"/> exists because a container and its database can start
    /// together: a deploy that failed because Postgres was two seconds behind would be
    /// indistinguishable from one that failed because the role is wrong, and the second is
    /// the only one worth stopping for. Connection failures are retried; a definitive
    /// answer of "this role bypasses RLS" is not.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The connection's role can bypass row-level security, or the database could not be
    /// reached within <paramref name="attempts"/>.
    /// </exception>
    public static async Task EnsureSubjectToRowLevelSecurityAsync(
        AppDbContext db,
        int attempts = 5,
        TimeSpan? delayBetweenAttempts = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        var delay = delayBetweenAttempts ?? TimeSpan.FromSeconds(2);
        Exception? lastFailure = null;

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            string role;
            bool bypassesRls;

            try
            {
                (role, bypassesRls) = await ReadRoleAsync(db, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Could not ask. The database is not up yet, or the credentials are wrong,
                // or the network is not there. All of them are worth one more try, and
                // none of them is the verdict below.
                lastFailure = ex;

                if (attempt < attempts)
                {
                    await Task.Delay(delay, cancellationToken);
                }

                continue;
            }

            if (bypassesRls)
            {
                // Deliberately outside the try: this is the answer, not a failure to get
                // one. Waiting does not change a role's attributes, so retrying it would
                // only delay the refusal it exists to cause.
                throw new InvalidOperationException(string.Format(
                    CultureInfo.InvariantCulture,
                    "The application is connected to Postgres as '{0}', which has BYPASSRLS. " +
                    "Every row-level security policy is inert on this connection while still " +
                    "reporting as enabled, so one tenant can read another tenant's data and " +
                    "nothing will say so. Connect as the non-owner application role " +
                    "(NOBYPASSRLS) instead; the owner account is for migrations only. " +
                    "See docs/ARCHITECTURE.md#multi-tenancy and Phase 1.6.",
                    role));
            }

            return;
        }

        throw new InvalidOperationException(
            $"Could not verify the database role after {attempts} attempts. Refusing to start: " +
            "an unverified connection may be bypassing row-level security. " +
            "The underlying failure is the inner exception.",
            lastFailure);
    }

    /// <summary>
    /// <c>rolbypassrls</c> for the role the connection is actually authenticated as.
    /// </summary>
    /// <remarks>
    /// <c>current_user</c> rather than <c>session_user</c>: what matters is the role whose
    /// privileges Postgres is applying, which a <c>SET ROLE</c> would change.
    /// <para>
    /// Both values come back from one statement as a single text column and are split
    /// here. EF's <c>SqlQuery&lt;T&gt;</c> maps by snake_case rather than by the alias in
    /// the SQL, so a two-column projection into a record would look up <c>bypasses_rls</c>,
    /// not find the alias it was given, and fail at runtime rather than at compile time —
    /// a trap this repository has already been caught by once.
    /// </para>
    /// </remarks>
    private static async Task<(string Role, bool BypassesRls)> ReadRoleAsync(
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var answer = await db.Database
            .SqlQuery<string>(
                $"SELECT current_user || ':' || (SELECT rolbypassrls FROM pg_roles WHERE rolname = current_user)::text AS \"Value\"")
            .SingleAsync(cancellationToken);

        var separator = answer.LastIndexOf(':');

        var role = answer[..separator];
        var bypasses = string.Equals(answer[(separator + 1)..], "true", StringComparison.Ordinal);

        return (role, bypasses);
    }
}
