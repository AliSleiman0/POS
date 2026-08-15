using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>One answer in a <see cref="ModifierGroup"/>, pointing at the product it adds.</summary>
/// <remarks>
/// <b>It holds no price.</b> The price is the product's, because the product is what ends up on
/// the order line and on the bill — a second price here would be a second thing to keep in step,
/// and the first time the two drifted the sheet would show one number and the bill charge
/// another. "Free" is a product priced at zero, which the engine handles like any other.
/// </remarks>
public sealed class ModifierOption : TenantEntity
{
    public Guid ModifierGroupId { get; set; }

    /// <summary>The product this option adds. Flagged <c>IsModifier</c>.</summary>
    public Guid ProductId { get; set; }

    public int SortOrder { get; set; }

    /// <summary>
    /// Whether this is the answer the till pre-selects.
    /// </summary>
    /// <remarks>
    /// Advisory and unconstrained, like <c>Barcode.IsPrimary</c>: nothing stops two, because a
    /// filtered unique index would turn "make this the default" into a clear-then-set across two
    /// saves. The sheet takes the first.
    /// </remarks>
    public bool IsDefault { get; set; }
}

/// <summary>Which groups a product asks about, and in what order.</summary>
/// <remarks>
/// A join table rather than a collection on either side, because the relationship is genuinely
/// many-to-many — "Cooked how?" hangs off every steak, and a steak asks three questions — and
/// because the ordering is a property of the pairing: "Cooked how?" comes before "Any sides?" on
/// a steak and might not on something else.
/// </remarks>
public sealed class ProductModifierGroup : TenantEntity
{
    public Guid ProductId { get; set; }

    public Guid ModifierGroupId { get; set; }

    public int SortOrder { get; set; }
}
