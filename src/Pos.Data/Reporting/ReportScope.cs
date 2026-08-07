namespace Pos.Data.Reporting;

/// <summary>
/// What a report covers: one shift, or one window of time.
/// </summary>
/// <remarks>
/// <b>One scope type for both, so the two reports cannot disagree about what is in scope.</b>
/// A Z-report for a shift and a daily report that contains that shift have to add up to the
/// same money, and the surest way to make that false is two sets of queries with two
/// hand-written <c>WHERE</c> clauses that drift.
/// <para>
/// The "no filter" values are sentinels rather than nulls — <see cref="Guid.Empty"/> and the
/// ends of time. A nullable parameter in an interpolated SQL query has to be cast on the
/// Postgres side to be comparable at all (<c>{id}::uuid IS NULL</c>), which is one more thing
/// to get subtly wrong for no benefit. No shift has an empty id and no sale is completed in
/// year one.
/// </para>
/// </remarks>
public readonly record struct ReportScope(Guid ShiftId, DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    /// <summary>Everything belonging to one shift, however long it ran.</summary>
    public static ReportScope ForShift(Guid shiftId) =>
        new(shiftId, DateTimeOffset.MinValue, DateTimeOffset.MaxValue);

    /// <summary>
    /// Everything in the half-open window <c>[start, end)</c> — a trading day, resolved by
    /// <c>BusinessDay.Range</c>.
    /// </summary>
    public static ReportScope ForRange(DateTimeOffset startUtc, DateTimeOffset endUtc) =>
        new(Guid.Empty, startUtc, endUtc);
}
