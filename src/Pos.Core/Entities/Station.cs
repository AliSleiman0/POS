using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A place in the kitchen that cooks things — the grill, the fryer, the bar, the pass.
/// </summary>
/// <remarks>
/// <b>A station is a screen, not a person and not a printer.</b> It is the unit a
/// <see cref="KitchenTicket"/> is addressed to, and the reason the model needs one at all is that
/// a single ticket holding the whole table is useless to a kitchen: the grill would read past
/// three drinks to find its steak, and the bar would pour it late. One ticket per station is what
/// makes each screen a work queue rather than a transcript.
/// <para>
/// <b>There is no hardware behind it.</b> Phase 10 has no printer support — that is a stated
/// non-goal, unchanged from Phase 6.2 — so a station is a row that a display filters on and
/// nothing more. A shop with one screen creates one station and every ticket lands on it, which is
/// the correct degenerate case rather than a special one.
/// </para>
/// <para>
/// Soft-deleted like <see cref="ServiceArea"/>, and for the same reason: tickets reference the
/// station they were sent to and stay readable for as long as the order does. A fryer taken out
/// for the winter still has last night's tickets hanging off it.
/// </para>
/// </remarks>
public sealed class Station : TenantEntity
{
    public const int NameMaxLength = 60;

    /// <summary>What the kitchen calls it — "Grill", "Bar", "Pass". Unique per tenant.</summary>
    public required string Name { get; set; }

    /// <summary>Where it sits in the station list. Not unique — ties fall back to the name.</summary>
    public int SortOrder { get; set; }

    /// <summary>Soft delete. See the remarks on the class.</summary>
    public bool IsActive { get; set; } = true;
}
