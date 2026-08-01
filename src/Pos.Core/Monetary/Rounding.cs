namespace Pos.Core.Monetary;

/// <summary>
/// How this system rounds a decimal, and what its numeric columns will hold. Stated once.
/// </summary>
/// <remarks>
/// <b>.NET's default is banker's rounding</b> — <c>decimal.Round(2.5m, 0)</c> is 2, not 3 —
/// and a receipt produced that way is one a customer will dispute and be right to.
/// <see cref="Mode"/> is therefore stated here and referenced everywhere, so "which way does
/// this round?" has a single answer a reader can check rather than infer. After Phase 3.1
/// there is exactly one <c>MidpointRounding</c> constant and one <c>decimal.Round</c> call
/// site in the repository, and they are both below.
/// <para>
/// Two scales, and the difference between them is CLAUDE.md invariant 3. Line extensions are
/// carried at <i>full</i> precision and stored at <see cref="StorageScale"/>; only the amount
/// a person actually pays is taken to <see cref="DisplayScale"/>, and only once. Rounding
/// each line to the payable scale and summing gives a total a cent or two from the honest
/// one — the classic penny-off bug, and the reason the Z-report would never balance.
/// </para>
/// <para>
/// The storage facts live here rather than beside the money type because a quantity is not
/// money and shares the same column shape: <c>stock_movement.quantity</c> is
/// <c>numeric(19,4)</c> for the same reason <c>sale.total</c> is.
/// </para>
/// </remarks>
public static class Rounding
{
    /// <summary>
    /// Half-away-from-zero: the "round half up" a shopper expects, in both directions.
    /// </summary>
    public const MidpointRounding Mode = MidpointRounding.AwayFromZero;

    /// <summary>Precision of the money and quantity columns: <c>numeric(19,4)</c>.</summary>
    public const int StoragePrecision = 19;

    /// <summary>
    /// Scale of the money and quantity columns.
    /// </summary>
    /// <remarks>
    /// Four rather than two, so unit prices like <c>0.1650</c> are exact and tax-inclusive
    /// back-calculation has room to work without accumulating error.
    /// </remarks>
    public const int StorageScale = 4;

    /// <summary>
    /// Scale of an amount a person pays: two places, the minor unit of the currency.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="StorageScale"/> on purpose. A sale line is stored at four
    /// places; the sale header's total is rounded to this, once, at the end of the pipeline.
    /// </remarks>
    public const int DisplayScale = 2;

    /// <summary>
    /// The largest value <c>numeric(19,4)</c> holds: 15 digits before the point, 4 after.
    /// </summary>
    public const decimal MaxStorable = 999_999_999_999_999.9999m;

    /// <summary>Rounds <paramref name="value"/> to <paramref name="scale"/> places.</summary>
    public static decimal To(decimal value, int scale) => decimal.Round(value, scale, Mode);

    /// <summary>
    /// Whether <paramref name="value"/> needs no more than <paramref name="scale"/> decimal
    /// places, so storing it at that scale cannot change it.
    /// </summary>
    /// <remarks>
    /// Compares against a rounded copy rather than reading the scale out of the decimal's
    /// representation, because <c>1.5000m</c> and <c>1.5m</c> are equal while carrying
    /// different scales — and a client that serialises a trailing zero has done nothing wrong.
    /// </remarks>
    public static bool IsExactAt(decimal value, int scale) => To(value, scale) == value;

    /// <summary>
    /// Whether <paramref name="value"/> survives <c>numeric(19,4)</c> exactly, in either
    /// direction.
    /// </summary>
    /// <remarks>
    /// The scale half of this matters because Postgres <i>rounds</i> rather than refusing:
    /// a price of 1.00005 stores as 1.0001 and the shop charges a hundredth of a cent nobody
    /// typed, with no error raised anywhere. Silence is the failure mode being prevented.
    /// </remarks>
    public static bool IsStorable(decimal value) =>
        value >= -MaxStorable && value <= MaxStorable && IsExactAt(value, StorageScale);
}
