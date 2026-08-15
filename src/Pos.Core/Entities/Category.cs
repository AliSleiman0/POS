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
    /// Which station cooks the things in it, inherited by sub-categories and by products.
    /// </summary>
    /// <remarks>
    /// <b>This is where a restaurant actually configures routing</b>, and the hierarchy above is
    /// what makes that tolerable: "Drinks → Bar" set once covers "Wine", "Beer" and "Soft", and
    /// the resolution walks up from the product until it finds one. <c>StationRouting</c> owns the
    /// walk and is pure.
    /// <para>
    /// Null means "ask my parent", not "nowhere" — and a product that reaches the top of the chain
    /// with nothing set is unrouted, which the fire endpoint refuses rather than guessing at.
    /// Retail shops have no stations and every category here is null, so nothing about a counter
    /// changes.
    /// </para>
    /// </remarks>
    public Guid? StationId { get; set; }

    /// <summary>
    /// Soft delete. Categories are never removed: products point at them, and so do
    /// historical reports.
    /// </summary>
    public bool IsActive { get; set; } = true;
}
