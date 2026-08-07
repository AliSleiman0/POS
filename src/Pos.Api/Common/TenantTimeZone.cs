namespace Pos.Api.Common;

/// <summary>
/// Resolves a tenant's <c>TimeZoneId</c> into the zone that <c>Pos.Core</c> is handed.
/// </summary>
/// <remarks>
/// Lives here rather than in Core because <see cref="TimeZoneInfo.FindSystemTimeZoneById"/>
/// reads the operating system's zone database, which is file access — invariant 1 keeps that
/// out of the domain layer. Core takes the resolved <see cref="TimeZoneInfo"/> as a parameter,
/// so its business-day and receipt logic is testable against a zone a test invents.
/// </remarks>
internal static class TenantTimeZone
{
    /// <summary>Resolves an IANA id, e.g. <c>Europe/Dublin</c>.</summary>
    /// <remarks>
    /// <b>An unknown id throws rather than falling back to UTC.</b> A silent fallback would
    /// print a receipt an hour out and put a late sale on the wrong trading day — both look
    /// entirely plausible on paper and neither would ever be reported as a bug. The id is set
    /// at onboarding and is not user input, so this is a misconfiguration to fix, not an error
    /// to absorb.
    /// <para>
    /// .NET resolves IANA ids on Windows as well as on Linux through ICU, so the same tenant
    /// row works on a developer machine and on the runner.
    /// </para>
    /// </remarks>
    public static TimeZoneInfo Resolve(string timeZoneId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new InvalidOperationException(
                $"The tenant's time zone '{timeZoneId}' is not a zone this host knows. Receipts "
                + "and trading-day reports cannot be produced until it is corrected to a valid "
                + "IANA id such as 'Europe/Dublin'.",
                exception);
        }
    }
}
