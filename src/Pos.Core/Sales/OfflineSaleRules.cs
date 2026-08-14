namespace Pos.Core.Sales;

/// <summary>
/// What the server will believe about a till's clock.
/// </summary>
/// <remarks>
/// A sale rung offline carries its own timestamp, because the alternative — dating it when the
/// network came back — puts a Tuesday-evening sale in Wednesday's Z-report and against a drawer
/// that was counted hours before. That is the reason <c>occurredAt</c> is accepted at all.
/// <para>
/// <b>Accepting it means trusting a clock nobody administers.</b> A shop tablet's clock can be
/// wrong by minutes from ordinary drift, and wrong by years from a flat battery or a factory
/// reset — and unlike a price, a timestamp is not something a cashier would notice being
/// absurd. So it is bounded in both directions here, once, in a pure function both the endpoint
/// and its tests read.
/// </para>
/// <para>
/// <b>Deliberately not validated against the shift's opening time.</b> The reconciliation UI
/// re-files a refused sale into whatever drawer is open now, carrying its original
/// <c>occurredAt</c> — which is, correctly, earlier than that shift. A rule forbidding it would
/// turn the review queue into a dead end, which is the one thing a review queue must not be.
/// </para>
/// </remarks>
public static class OfflineSaleRules
{
    /// <summary>
    /// How far ahead of the server a till's clock may be and still be believed.
    /// </summary>
    /// <remarks>
    /// Small, because there is no legitimate reason for a sale to be in the future at all — the
    /// allowance exists for clock skew between two machines, not for early trading. Wide enough
    /// to absorb an unsynchronised tablet, narrow enough that a sale cannot be booked into
    /// tomorrow's trading day from today's.
    /// </remarks>
    public static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest offline window the server will accept a sale from.
    /// </summary>
    /// <remarks>
    /// Three days, and the number is a judgement rather than a law. It has to be longer than a
    /// bank-holiday weekend, because a shop whose line goes down on Friday and comes back on
    /// Tuesday must not have its takings refused. It has to be short enough that a till with a
    /// badly wrong clock — the flat-battery case, which typically lands years out — is caught
    /// rather than silently writing history.
    /// <para>
    /// Beyond it the sale is not discarded. It is refused with a permanent error, which puts it
    /// in the outbox's review queue where a person decides what happened, and that is the right
    /// outcome for a sale nobody can date.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan MaxOfflineWindow = TimeSpan.FromDays(3);

    /// <summary>
    /// Whether <paramref name="occurredAt"/> is a time this server will date a sale by.
    /// </summary>
    /// <param name="occurredAt">The till's claim about when the customer paid.</param>
    /// <param name="now">The server's clock.</param>
    /// <returns>
    /// <see langword="true"/> when it falls within <see cref="MaxClockSkew"/> ahead of
    /// <paramref name="now"/> and <see cref="MaxOfflineWindow"/> behind it.
    /// </returns>
    public static bool IsAcceptableOccurredAt(DateTimeOffset occurredAt, DateTimeOffset now) =>
        occurredAt <= now + MaxClockSkew && occurredAt >= now - MaxOfflineWindow;

    /// <summary>
    /// Why a timestamp was refused, in words a cashier's manager can act on.
    /// </summary>
    /// <remarks>
    /// Returned to the client and shown in the review queue, so it names the likely cause
    /// rather than restating the bound. "This till's clock is wrong" is something a shop can
    /// fix; "occurredAt out of range" is not.
    /// </remarks>
    public static string DescribeRefusal(DateTimeOffset occurredAt, DateTimeOffset now) =>
        occurredAt > now + MaxClockSkew
            ? "That sale is dated in the future, so this till's clock is ahead of the server's. "
              + "Correct the till's date and time, then send it again."
            : $"That sale is dated more than {MaxOfflineWindow.TotalDays:0} days ago. Either it "
              + "has been queued that long, or this till's clock is wrong. A manager needs to "
              + "confirm what was taken before it can be recorded.";
}
