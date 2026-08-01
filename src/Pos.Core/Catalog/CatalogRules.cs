using Pos.Core.Monetary;

namespace Pos.Core.Catalog;

/// <summary>
/// What the catalog's numeric columns will accept, stated once so the API and the tests
/// agree on the boundaries.
/// </summary>
/// <remarks>
/// <b>This is not the <see cref="Money"/> type</b>, which owns arithmetic and rounding.
/// These are validation predicates over values that have not been stored yet — the question
/// is only "would the column hold this exactly?", which is a different and much smaller
/// question than "how do we add two prices?".
/// <para>
/// The two were kept separate when 3.1 landed, deliberately: <see cref="IsValidTaxRate"/> is
/// a rate and <see cref="IsStorableSignedAmount"/> is what <c>StockRules.IsStorableQuantity</c>
/// calls for a <i>quantity</i>, and neither of those is money. Folding them into
/// <see cref="Money"/> would make a kilogram a currency. What they now share is the
/// <see cref="Rounding"/> primitive, so there is one rounding mode and one storage scale in
/// the system rather than two that agree until the day they do not.
/// </para>
/// </remarks>
public static class CatalogRules
{
    /// <summary>Scale of the money and quantity columns: <c>numeric(19,4)</c>.</summary>
    public const int AmountScale = Rounding.StorageScale;

    /// <summary>
    /// The largest value <c>numeric(19,4)</c> holds: 15 digits before the point, 4 after.
    /// </summary>
    public const decimal MaxAmount = Rounding.MaxStorable;

    /// <summary>Scale of <c>tax_class.rate</c>, which is narrower at <c>numeric(6,4)</c>.</summary>
    public const int RateScale = Rounding.StorageScale;

    /// <summary>
    /// Whether a price or quantity is non-negative and survives the column exactly.
    /// </summary>
    public static bool IsStorableAmount(decimal value) =>
        value >= 0m && IsStorableSignedAmount(value);

    /// <summary>
    /// Whether a value survives <c>numeric(19,4)</c> exactly, in either direction.
    /// </summary>
    /// <remarks>
    /// Split out for the stock ledger, where a movement quantity is signed — waste and sales
    /// are negative. Sharing the range and scale check with <see cref="IsStorableAmount"/>
    /// rather than restating it is what stops the two drifting the day the column changes.
    /// </remarks>
    public static bool IsStorableSignedAmount(decimal value) => Rounding.IsStorable(value);

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
        value >= 0m && value <= 1m && Rounding.IsExactAt(value, RateScale);
}
