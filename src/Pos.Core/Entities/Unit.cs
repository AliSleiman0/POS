namespace Pos.Core.Entities;

/// <summary>
/// The unit a product is sold in, which decides whether a quantity can be fractional.
/// </summary>
/// <remarks>
/// Not cosmetic: it is what makes 0.350 kg of cheese an ordinary sale rather than a
/// rounding argument at the till. Quantities are <c>numeric(19,4)</c> for every unit —
/// this says which ones a cashier may legitimately type a fraction into.
/// <para>
/// Persisted as text, and the allowed values in the database are generated from these
/// member names by <c>HasEnumAsText</c>. Renaming a member therefore rewrites a check
/// constraint and orphans every row already written with the old name; a test pins the
/// names for exactly that reason.
/// </para>
/// </remarks>
public enum Unit
{
    /// <summary>
    /// Discrete items. <b>Zero on purpose</b>, so a row written without a unit — by an
    /// import, a script, or a future bug — is a countable item rather than a weight.
    /// </summary>
    Each = 0,

    /// <summary>Sold by weight. Quantities are fractional.</summary>
    Kilogram = 1,

    /// <summary>Sold by volume. Quantities are fractional.</summary>
    Litre = 2,
}
