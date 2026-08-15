using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One table in the room.
/// </summary>
/// <remarks>
/// <b>Named <c>DiningTable</c> rather than <c>Table</c>, and the database table is
/// <c>dining_table</c>.</b> <c>TABLE</c> is a reserved word in SQL, so the plain name would have
/// to be quoted in every hand-written query — and this codebase has hand-written queries in the
/// money paths, because EF cannot aggregate a value-converted <c>Money</c>. A name that only
/// works when somebody remembers to quote it is a name that will eventually not be quoted.
/// <para>
/// <b>It holds no state about being occupied.</b> "Is table 4 free?" is answered by asking
/// whether an open <see cref="Order"/> points at it, which is one question with one answer. A
/// boolean here would be a second copy of that fact, and the two would disagree the first time a
/// request failed between updating one and the other — leaving a table that reads as busy with
/// nothing on it, or worse, free with a bill still open.
/// </para>
/// </remarks>
public sealed class DiningTable : TenantEntity
{
    public const int NameMaxLength = 40;

    public Guid ServiceAreaId { get; set; }

    /// <summary>What staff call it — "4", "12a", "Window". Text, because half of them are not numbers.</summary>
    public required string Name { get; set; }

    /// <summary>
    /// How many it seats, for the floor view and for a cover count that defaults sensibly.
    /// </summary>
    /// <remarks>
    /// Advisory, never enforced. Six people sit at a four-top constantly, and a till that refused
    /// the fifth cover would be wrong about the room rather than the room being wrong about
    /// itself.
    /// </remarks>
    public int Seats { get; set; }

    public int SortOrder { get; set; }

    /// <summary>Soft delete, for the reason <see cref="ServiceArea.IsActive"/> is.</summary>
    public bool IsActive { get; set; } = true;
}
