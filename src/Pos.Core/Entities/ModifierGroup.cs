using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A question the till asks when an item is ordered — "how would you like it cooked?".
/// </summary>
/// <remarks>
/// The group carries the <b>rule</b>; <see cref="ModifierOption"/> carries the answers. Keeping
/// them apart is what lets one group ("Cooked how?") hang off every steak on the menu without
/// its options being retyped per dish — and what makes adding "blue" a single row rather than an
/// edit of twelve products.
/// </remarks>
public sealed class ModifierGroup : TenantEntity
{
    public const int NameMaxLength = 80;

    /// <summary>What the till asks. Shown as the sheet's heading.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// The fewest options that satisfy this group. Zero means it can be skipped.
    /// </summary>
    /// <remarks>
    /// A minimum of 1 is how "the kitchen cannot cook this without an answer" is expressed, and
    /// it is enforced at the API rather than only in the sheet — a client that skipped the
    /// question would otherwise send a steak to the grill with no temperature on it.
    /// </remarks>
    public int MinSelections { get; set; }

    /// <summary>
    /// The most options allowed, or null for no limit.
    /// </summary>
    /// <remarks>
    /// Null rather than a large number, because "as many toppings as you like" is a real menu
    /// and a sentinel would eventually be hit by somebody ordering a pizza.
    /// </remarks>
    public int? MaxSelections { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Soft delete, like the catalog's: order lines reference what was chosen.</summary>
    public bool IsActive { get; set; } = true;
}
