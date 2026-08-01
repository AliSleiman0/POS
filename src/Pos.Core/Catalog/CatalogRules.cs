namespace Pos.Core.Catalog;

/// <summary>
/// What the catalog's numeric columns will accept, stated once so the API and the tests
/// agree on the boundaries.
/// </summary>
/// <remarks>
/// <b>This is not the <c>Money</c> type.</b> Phase 3.1 introduces that, and it owns
/// arithmetic and rounding. These are validation predicates over values that have not been
/// stored yet — the question is only "would the column hold this exactly?", which is a
/// different and much smaller question than "how do we add two prices?".
/// <para>
/// The point of checking scale is that Postgres <i>rounds</i> rather than refusing. A price
/// of 1.00005 stores as 1.0001 and the shop is charging a hundredth of a cent it never
/// entered, with no error anywhere. Silence is the failure mode being prevented here.
/// </para>
/// </remarks>
public static class CatalogRules
{
    /// <summary>Scale of the money and quantity columns: <c>numeric(19,4)</c>.</summary>
    public const int AmountScale = 4;

    /// <summary>
    /// The largest value <c>numeric(19,4)</c> holds: 15 digits before the point, 4 after.
    /// </summary>
    public const decimal MaxAmount = 999_999_999_999_999.9999m;

    /// <summary>Scale of <c>tax_class.rate</c>, which is narrower at <c>numeric(6,4)</c>.</summary>
    public const int RateScale = 4;

    /// <summary>
    /// Whether a price or quantity is non-negative and survives the column exactly.
    /// </summary>
    public static bool IsStorableAmount(decimal value) =>
        value >= 0m && value <= MaxAmount && HasScaleAtMost(value, AmountScale);

    /// <summary>
    /// Whether a tax rate is a fraction the <c>ck_tax_class_rate_range</c> check will accept
    /// and <c>numeric(6,4)</c> will hold exactly.
    /// </summary>
    /// <remarks>
    /// Inclusive at both ends, mirroring the constraint. Zero is a real rate (zero-rated
    /// goods are a category, not an absence), and 1 is the degenerate but legal 100%.
    /// A rate is a fraction: 20% is <c>0.2000</c>, and <c>20</c> is four hundred times too
    /// much rather than a unit mix-up the code can guess at.
    /// </remarks>
    public static bool IsValidTaxRate(decimal value) =>
        value >= 0m && value <= 1m && HasScaleAtMost(value, RateScale);

    /// <summary>
    /// Whether <paramref name="value"/> needs no more than <paramref name="scale"/> decimal
    /// places, so storing it cannot round it.
    /// </summary>
    /// <remarks>
    /// Compares against a rounded copy rather than reading the scale out of the decimal's
    /// representation, because 1.5000m and 1.5m are equal but carry different scales — and
    /// a client that serialises a trailing zero has not done anything wrong.
    /// </remarks>
    private static bool HasScaleAtMost(decimal value, int scale) =>
        decimal.Round(value, scale, MidpointRounding.AwayFromZero) == value;
}
