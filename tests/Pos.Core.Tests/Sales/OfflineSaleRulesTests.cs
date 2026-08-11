using Pos.Core.Sales;

namespace Pos.Core.Tests.Sales;

/// <summary>
/// The bounds on a till's claim about when a customer paid.
/// </summary>
/// <remarks>
/// Pure and cheap, so the boundaries are asserted on both sides rather than sampled in the
/// middle. The values themselves are judgements — see <see cref="OfflineSaleRules"/> — so these
/// read the constants rather than hard-coding three days and five minutes, and stay true if the
/// judgement changes.
/// </remarks>
public sealed class OfflineSaleRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 11, 14, 30, 0, TimeSpan.Zero);

    [Fact]
    public void The_server_s_own_moment_is_acceptable()
    {
        Assert.True(OfflineSaleRules.IsAcceptableOccurredAt(Now, Now));
    }

    [Fact]
    public void A_sale_from_the_middle_of_an_offline_afternoon_is_acceptable()
    {
        Assert.True(OfflineSaleRules.IsAcceptableOccurredAt(Now.AddHours(-6), Now));
    }

    [Fact]
    public void A_bank_holiday_weekend_offline_is_acceptable()
    {
        // The case the window exists for: the line goes down on Friday and comes back on
        // Tuesday. A shop's takings must not be refused because nobody was there to fix it.
        Assert.True(OfflineSaleRules.IsAcceptableOccurredAt(Now.AddDays(-2).AddHours(-20), Now));
    }

    [Fact]
    public void The_far_edge_of_the_offline_window_is_acceptable()
    {
        Assert.True(
            OfflineSaleRules.IsAcceptableOccurredAt(Now - OfflineSaleRules.MaxOfflineWindow, Now));
    }

    [Fact]
    public void A_sale_older_than_the_offline_window_is_refused()
    {
        Assert.False(OfflineSaleRules.IsAcceptableOccurredAt(
            Now - OfflineSaleRules.MaxOfflineWindow - TimeSpan.FromSeconds(1),
            Now));
    }

    [Fact]
    public void A_till_whose_clock_lost_years_is_refused()
    {
        // The flat-battery case. It lands far enough out that no window would admit it, which
        // is the point of having one at all.
        Assert.False(OfflineSaleRules.IsAcceptableOccurredAt(Now.AddYears(-6), Now));
    }

    [Fact]
    public void Clock_skew_within_the_allowance_is_acceptable()
    {
        // A tablet a couple of minutes ahead of the server is ordinary and must not be a
        // refusal — there is no shop in which that is a problem worth surfacing to a cashier.
        Assert.True(
            OfflineSaleRules.IsAcceptableOccurredAt(Now + OfflineSaleRules.MaxClockSkew, Now));
    }

    [Fact]
    public void A_sale_further_into_the_future_than_the_allowance_is_refused()
    {
        Assert.False(OfflineSaleRules.IsAcceptableOccurredAt(
            Now + OfflineSaleRules.MaxClockSkew + TimeSpan.FromSeconds(1),
            Now));
    }

    [Fact]
    public void A_sale_dated_tomorrow_is_refused()
    {
        // The one that would otherwise book takings into a trading day that has not happened,
        // where they would be invisible in today's report and appear in tomorrow's from nowhere.
        Assert.False(OfflineSaleRules.IsAcceptableOccurredAt(Now.AddDays(1), Now));
    }

    [Fact]
    public void The_refusal_for_a_future_sale_names_the_clock()
    {
        // A message a shop can act on. "Out of range" is not one; "this till's clock is ahead"
        // tells somebody what to go and change.
        var message = OfflineSaleRules.DescribeRefusal(Now.AddDays(1), Now);

        Assert.Contains("clock", message, StringComparison.Ordinal);
        Assert.Contains("future", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_for_a_stale_sale_asks_for_a_manager()
    {
        var message = OfflineSaleRules.DescribeRefusal(Now.AddYears(-6), Now);

        Assert.Contains("manager", message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_window_is_long_enough_for_a_three_day_weekend()
    {
        // Guards the judgement rather than restating it: shortening this below a long weekend
        // would refuse the exact scenario the feature exists for, and should be a deliberate
        // decision that fails a test first.
        Assert.True(OfflineSaleRules.MaxOfflineWindow >= TimeSpan.FromDays(3));
    }

    [Fact]
    public void The_forward_allowance_is_far_shorter_than_a_trading_day()
    {
        // Asymmetry is the design: hours of lateness are ordinary, and any amount of earliness
        // is a broken clock. If the allowance ever approached a day it could move a sale into
        // the next trading day, which is the failure the bound exists to prevent.
        Assert.True(OfflineSaleRules.MaxClockSkew < TimeSpan.FromHours(1));
    }
}
