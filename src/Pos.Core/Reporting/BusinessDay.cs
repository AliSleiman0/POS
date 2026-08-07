namespace Pos.Core.Reporting;

/// <summary>
/// A tenant's trading day: which instants belong to it, and which one an instant belongs to.
/// </summary>
/// <remarks>
/// CLAUDE.md invariant 8 — UTC everywhere, business day at the edge. A shop that closes at
/// 02:00 has not started Tuesday yet, and a report that said otherwise would disagree with
/// every member of staff who worked the shift. Getting this wrong makes every daily figure
/// wrong by a few hours' takings, which destroys trust in the whole reporting section without
/// ever looking like a bug.
/// <para>
/// <b>The zone is a parameter, never looked up.</b> <c>TimeZoneInfo.FindSystemTimeZoneById</c>
/// reads the operating system's zone database, which is file access, and invariant 1 keeps that
/// out of Core. <c>Pos.Api.Common.TenantTimeZone</c> resolves it. The side benefit is that these
/// rules can be tested against a zone a test invents, rather than against whatever the machine
/// running the suite happens to have installed.
/// </para>
/// </remarks>
public static class BusinessDay
{
    /// <summary>
    /// The half-open UTC range <c>[start, end)</c> covering <paramref name="day"/>.
    /// </summary>
    /// <param name="day">The trading day, in the tenant's own calendar.</param>
    /// <param name="zone">The tenant's zone.</param>
    /// <param name="dayStart">
    /// How far past local midnight the trading day begins. <c>04:00</c> puts a 02:00 close on
    /// the previous day.
    /// </param>
    /// <remarks>
    /// <b>Half-open, so consecutive days tile exactly.</b> An inclusive end would count a sale
    /// struck at the boundary on both days, and a report that double-counts is worse than one
    /// that is merely wrong — the totals still reconcile against each other and only fail
    /// against the drawer.
    /// </remarks>
    public static (DateTimeOffset StartUtc, DateTimeOffset EndUtc) Range(
        DateOnly day,
        TimeZoneInfo zone,
        TimeSpan dayStart)
    {
        ArgumentNullException.ThrowIfNull(zone);

        if (dayStart < TimeSpan.Zero || dayStart >= TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dayStart),
                dayStart,
                "A business day starts between 00:00 and 24:00 after local midnight.");
        }

        return (StartOf(day, zone, dayStart), StartOf(day.AddDays(1), zone, dayStart));
    }

    /// <summary>
    /// Which trading day <paramref name="instant"/> falls in.
    /// </summary>
    /// <remarks>
    /// <b>Defined in terms of <see cref="Range"/> rather than computed independently</b>, and
    /// that is deliberate. The obvious implementation — convert to local, subtract
    /// <paramref name="dayStart"/>, take the date — disagrees with <c>Range</c> by an hour on
    /// the two days a year the clocks move. A sale would then be listed under a day whose own
    /// report excluded it, which is precisely the kind of discrepancy that gets blamed on the
    /// till rather than on the calendar.
    /// </remarks>
    public static DateOnly Of(DateTimeOffset instant, TimeZoneInfo zone, TimeSpan dayStart)
    {
        ArgumentNullException.ThrowIfNull(zone);

        var local = TimeZoneInfo.ConvertTime(instant, zone);

        // The candidate is right on all but the transition days; the walk below settles those.
        var candidate = DateOnly.FromDateTime(local.DateTime.Add(-dayStart));

        // At most one step in either direction: a DST shift is an hour or two, never a day.
        for (var step = -1; step <= 1; step++)
        {
            var day = candidate.AddDays(step);
            var (start, end) = Range(day, zone, dayStart);

            if (instant >= start && instant < end)
            {
                return day;
            }
        }

        // Unreachable while the days tile, which they do by construction. Loud rather than
        // silently returning the candidate: a gap here would mean sales belonging to no day.
        throw new InvalidOperationException(
            $"{instant:O} fell into no trading day around {candidate:O} for {zone.Id} "
            + $"with a day start of {dayStart}. The business-day ranges are not tiling.");
    }

    /// <summary>
    /// The instant a trading day begins, in UTC.
    /// </summary>
    /// <remarks>
    /// <b>The two days a year this has to survive</b>, and neither may throw — a Z-report that
    /// crashed on the last Sunday in March would be a shop that cannot cash up.
    /// <list type="bullet">
    /// <item><b>Spring forward</b> skips a range of local times, so a 01:30 day start does not
    /// exist that morning. <c>ConvertTimeToUtc</c> throws on one. The offset in force before the
    /// clocks moved is used instead, which lands on the first instant at or after the jump.</item>
    /// <item><b>Autumn back</b> repeats a range, so 01:30 happens twice. The <i>earlier</i>
    /// occurrence is taken — the day starts the first time the clock reads 01:30, which is what
    /// anyone in the shop would say.</item>
    /// </list>
    /// Both choices cost at most an hour at one boundary, and neither breaks tiling: the end of
    /// a day is defined as the start of the next one, so whatever this returns, no instant is
    /// counted twice and none is lost.
    /// </remarks>
    private static DateTimeOffset StartOf(DateOnly day, TimeZoneInfo zone, TimeSpan dayStart)
    {
        var local = day.ToDateTime(TimeOnly.MinValue).Add(dayStart);

        var offset = zone.IsAmbiguousTime(local)

            // Two offsets are valid; the larger one is daylight time, which is the earlier of
            // the two occurrences.
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.IsInvalidTime(local)

                // A day earlier is safely outside the transition, so this is the offset that
                // was in force when the clocks jumped forward past this local time.
                ? zone.GetUtcOffset(local.AddDays(-1))
                : zone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}
