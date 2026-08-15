using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A part of the room tables are grouped into — the bar, the terrace, upstairs.
/// </summary>
/// <remarks>
/// <b>A grouping, not a floor plan.</b> There are no coordinates here and there is no canvas
/// editor: an area has a name and a sort order, and its tables list under it. A drag-and-drop
/// designer is a week of work that changes nothing about whether the till is correct, and it is
/// listed as a non-goal in the phase doc rather than left to look like an oversight.
/// <para>
/// It earns a table of its own rather than a string on <see cref="DiningTable"/> because staff
/// filter by it constantly ("what is open on the terrace?") and because a renamed area must not
/// leave half its tables behind under the old spelling.
/// </para>
/// </remarks>
public sealed class ServiceArea : TenantEntity
{
    public const int NameMaxLength = 60;

    public required string Name { get; set; }

    /// <summary>Where it sits in the floor view. Not unique — ties fall back to the name.</summary>
    public int SortOrder { get; set; }

    /// <summary>
    /// Soft delete, like the catalog's.
    /// </summary>
    /// <remarks>
    /// Never hard-deleted, for the reason a product is not: orders reference the table they were
    /// served at, and reports read back through that reference for months. A terrace closed for
    /// the winter still has last summer's takings hanging off it.
    /// </remarks>
    public bool IsActive { get; set; } = true;
}
