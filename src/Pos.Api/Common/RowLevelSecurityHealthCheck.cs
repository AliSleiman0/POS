using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pos.Data;
using Pos.Data.Security;

namespace Pos.Api.Common;

/// <summary>
/// Reports unhealthy if the database connection's role can bypass row-level security.
/// </summary>
/// <remarks>
/// The rule itself is <see cref="DatabaseRoleGuard"/> in <c>Pos.Data</c>; this is only the
/// hosting shape of it.
/// <para>
/// <b>Why a readiness check rather than a startup refusal.</b> Both stop a bad deploy: a
/// machine whose readiness check fails receives no traffic and the deploy is rolled back,
/// which is the same protection as refusing to boot, expressed in the platform's own
/// vocabulary. A readiness check is better in three ways — it does not couple building the
/// host to the database being reachable, it does not turn a database outage into a restart
/// loop, and it keeps checking. A boot-time assertion answers "was the role right when this
/// machine last started", which after six weeks of uptime is a claim about history.
/// </para>
/// <para>
/// Tagged <c>ready</c>, so it gates traffic and never liveness. Restarting the process
/// cannot fix a connection string, and killing a machine over it would only produce a crash
/// loop with the same wrong role each time.
/// </para>
/// <para>
/// The response body stays "Unhealthy" and nothing more — <c>/health/ready</c> is anonymous
/// and must not describe the deployment to whoever asks. The reason goes to the logs, which
/// is where somebody debugging a failed deploy is already looking.
/// </para>
/// </remarks>
public sealed class RowLevelSecurityHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // One attempt: the retry budget in the guard exists for a container racing its
            // database at startup, and a health check that is polled every fifteen seconds
            // already has all the retries it needs.
            await DatabaseRoleGuard.EnsureSubjectToRowLevelSecurityAsync(
                db,
                attempts: 1,
                cancellationToken: cancellationToken);

            return HealthCheckResult.Healthy();
        }
        catch (InvalidOperationException ex)
        {
            // Unhealthy rather than a rethrow, so the description reaches the log the
            // health-check middleware writes rather than surfacing as an unhandled
            // exception from a probe.
            return HealthCheckResult.Unhealthy(
                "The database connection is not subject to row-level security.",
                ex);
        }
    }
}
