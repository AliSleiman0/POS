using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A grouping of products, for browsing on the register grid and for reporting.
/// </summary>
/// <remarks>
/// Hierarchical, because real shops group that way — "Deli" contains "Cheese" — and a flat
/// list forces either duplicated names or a category count nobody can navigate on a till.
/// <para>
/// A product belongs to at most one category, deliberately. Many-to-many tagging is a
/// merchandising feature; it is not what a cashier needs to find an unbarcoded item in
/// three taps, and it makes "sales by category" ambiguous.
/// </para>
/// </remarks>
public sealed class Category : TenantEntity
{
    public const int NameMaxLength = 100;

    public required string Name { get; set; }

    /// <summary>
    /// The parent, or null for a top-level category.
    /// </summary>
    /// <remarks>
    /// The database guarantees the parent is a category <i>in the same tenant</i> — the
    /// foreign key carries <c>TenantId</c>. It cannot guarantee the graph is acyclic;
    /// nothing in SQL can, so the endpoint that sets this checks for a cycle.
    /// </remarks>
    public Guid? ParentCategoryId { get; set; }

    /// <summary>Display order within the parent. Not unique — ties fall back to name.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Soft delete. Categories are never removed: products point at them, and so do
    /// historical reports.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
