using Pos.Core.Reporting;

namespace Pos.Core.Tests.Reporting;

/// <summary>
/// The trading day.
/// </summary>
/// <remarks>
/// The exit criterion §6.3 states — a 23:30 and a 01:30 sale under a 04:00 day start — plus the
/// two days a year the clocks move, which is where an implementation that passes every ordinary
/// test throws or silently loses an hour of takings.
/// <para>
/// Europe/Dublin is used for the daylight-saving cases and is a real zone from the host's
/// database, which is exactly what <c>InvariantGlobalization</c> made unresolvable on Windows
/// until Phase 6.1 turned it off. The rest use a fixed-offset zone the test invents, so the
/// ordinary arithmetic does not depend on any zone's politics.
/// </para>
/// </remarks>
public sealed class BusinessDayTests
{
    private static readonly TimeZoneInfo Fixed = TimeZoneInfo.CreateCustomTimeZone(
        "Test/PlusTwo", TimeSpan.FromHours(2), "Test +02:00", "Test +02:00");

    private static readonly TimeZoneInfo Dublin = TimeZoneInfo.FindSystemTimeZoneById("Europe/Dublin");

    private static readonly TimeSpan FourAm = TimeSpan.FromHours(4);

    [Fact]
    public void A_day_runs_from_its_start_to_the_next_days_start()
    {
        var (start, end) = BusinessDay.Range(new DateOnly(2026, 8, 7), Fixed, FourAm);

        // 04:00 local at +02:00 is 02:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 8, 7, 2, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 2, 0, 0, TimeSpan.Zero), end);
    }

    [Fact]
    public void A_2330_and_a_0130_sale_land_on_the_days_the_staff_would_say()
    {
        // §6.3's exit criterion, and the whole reason the offset exists. The 01:30 sale was rung
        // by the person still closing up on Friday night; a report that filed it under Saturday
        // would disagree with everyone who was there.
        var friday = new DateOnly(2026, 8, 7);

        var lateEvening = Local(2026, 8, 7, 23, 30);
        var earlyMorning = Local(2026, 8, 8, 1, 30);

        Assert.Equal(friday, BusinessDay.Of(lateEvening, Fixed, FourAm));
        Assert.Equal(friday, BusinessDay.Of(earlyMorning, Fixed, FourAm));

        // And 04:00 the next morning is where the new day starts, not midnight.
        Assert.Equal(friday, BusinessDay.Of(Local(2026, 8, 8, 3, 59), Fixed, FourAm));
        Assert.Equal(friday.AddDays(1), BusinessDay.Of(Local(2026, 8, 8, 4, 0), Fixed, FourAm));
    }

    [Fact]
    public void With_no_offset_the_trading_day_is_the_calendar_day()
    {
        // The default, and the common case: a shop that closes before midnight.
        var day = new DateOnly(2026, 8, 7);
        var (start, end) = BusinessDay.Range(day, Fixed, TimeSpan.Zero);

        Assert.Equal(new DateTimeOffset(2026, 8, 6, 22, 0, 0, TimeSpan.Zero), start);
        Assert.Equal(new DateTimeOffset(2026, 8, 7, 22, 0, 0, TimeSpan.Zero), end);
        Assert.Equal(day, BusinessDay.Of(Local(2026, 8, 7, 12, 0), Fixed, TimeSpan.Zero));
    }

    [Fact]
    public void The_start_of_a_day_belongs_to_it_and_the_end_belongs_to_the_next()
    {
        // Half-open, stated as an assertion. An inclusive end double-counts the sale struck
        // exactly at the boundary, and the totals still reconcile against each other — so the
        // only place it shows up is against the cash in the drawer.
        var day = new DateOnly(2026, 8, 7);
        var (start, end) = BusinessDay.Range(day, Fixed, FourAm);

        Assert.Equal(day, BusinessDay.Of(start, Fixed, FourAm));
        Assert.Equal(day, BusinessDay.Of(end.AddTicks(-1), Fixed, FourAm));
        Assert.Equal(day.AddDays(1), BusinessDay.Of(end, Fixed, FourAm));
    }

    [Theory]
    [InlineData(2026, 3, 28)]  // the day before the spring transition
    [InlineData(2026, 3, 29)]  // Europe/Dublin springs forward: 01:00 → 02:00
    [InlineData(2026, 3, 30)]
    [InlineData(2026, 10, 24)]
    [InlineData(2026, 10, 25)] // and falls back: 02:00 → 01:00
    [InlineData(2026, 10, 26)]
    public void A_day_across_a_clock_change_is_produced_rather_than_thrown(int year, int month, int number)
    {
        // A Z-report that crashed on the last Sunday in March is a shop that cannot cash up.
        // Both transitions are exercised at a day start of 01:30, which is inside Dublin's
        // spring gap and inside its autumn repeat — the two local times that do not exist once
        // and exist twice.
        var day = new DateOnly(year, month, number);
        var halfPastOne = new TimeSpan(1, 30, 0);

        var (start, end) = BusinessDay.Range(day, Dublin, halfPastOne);

        Assert.True(start < end, $"{day} produced an empty or inverted range: {start:O} to {end:O}.");

        // 23 or 25 hours on a transition day, and the whole of it belongs to this day.
        Assert.Equal(day, BusinessDay.Of(start, Dublin, halfPastOne));
        Assert.Equal(day, BusinessDay.Of(end.AddTicks(-1), Dublin, halfPastOne));
    }

    [Fact]
    public void Consecutive_days_tile_with_no_gap_and_no_overlap_across_a_transition()
    {
        // The property that actually matters. Whatever the two odd mornings do to the length of
        // a day, no instant may belong to two days and none to none — a sale in a gap is
        // takings that appear in no report at all, and nothing would surface it.
        var halfPastOne = new TimeSpan(1, 30, 0);

        foreach (var start in new[] { new DateOnly(2026, 3, 27), new DateOnly(2026, 10, 23) })
        {
            for (var offset = 0; offset < 4; offset++)
            {
                var day = start.AddDays(offset);

                var today = BusinessDay.Range(day, Dublin, halfPastOne);
                var tomorrow = BusinessDay.Range(day.AddDays(1), Dublin, halfPastOne);

                Assert.Equal(today.EndUtc, tomorrow.StartUtc);
            }
        }
    }

    [Fact]
    public void Every_minute_of_a_transition_day_belongs_to_exactly_one_trading_day()
    {
        // The same property from the other side, and the one that would catch `Of` being
        // reimplemented as "convert, subtract, take the date" — which is right on 363 days a
        // year and an hour out on the other two.
        var halfPastOne = new TimeSpan(1, 30, 0);

        foreach (var day in new[] { new DateOnly(2026, 3, 29), new DateOnly(2026, 10, 25) })
        {
            var (start, end) = BusinessDay.Range(day, Dublin, halfPastOne);

            for (var instant = start; instant < end; instant = instant.AddMinutes(30))
            {
                Assert.Equal(day, BusinessDay.Of(instant, Dublin, halfPastOne));
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(24)]
    public void A_day_start_outside_a_day_is_refused(int hours)
    {
        // Not clamped. An offset of 25 hours is a configuration mistake, and a report quietly
        // covering the wrong window is worse than one that will not run.
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BusinessDay.Range(new DateOnly(2026, 8, 7), Fixed, TimeSpan.FromHours(hours)));
    }

    /// <summary>An instant expressed as the tenant's own wall clock.</summary>
    private static DateTimeOffset Local(int year, int month, int day, int hour, int minute) =>
        new(year, month, day, hour, minute, 0, Fixed.BaseUtcOffset);
}
