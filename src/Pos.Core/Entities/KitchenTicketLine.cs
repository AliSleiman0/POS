using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One thing to cook, exactly as the kitchen was told it.
/// </summary>
/// <remarks>
/// <b>Every field is a snapshot, including the modifiers.</b> Nothing here is read back off
/// <see cref="OrderLine"/> — see the remarks on <see cref="KitchenTicket"/> for why. A product
/// renamed mid-service does not rewrite the tickets already on the screen, which is the same rule
/// invariant 5 states for money and the same argument applied to food.
/// </remarks>
public sealed class KitchenTicketLine : TenantEntity
{
    public const int DescriptionMaxLength = 200;
    public const int ModifierTextMaxLength = 500;
    public const int NoteMaxLength = 200;

    public Guid KitchenTicketId { get; set; }

    /// <summary>
    /// The order line this came from.
    /// </summary>
    /// <remarks>
    /// Provenance, and what a void is matched against so the display can strike the line through.
    /// It is <b>not</b> what the ticket is rendered from — everything shown is on this row.
    /// </remarks>
    public Guid OrderLineId { get; set; }

    /// <summary>The order's line number, so "void line 7" means one thing on both screens.</summary>
    public int LineNumber { get; set; }

    /// <summary>The product's name as of firing.</summary>
    public required string Description { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>Which seat it is for, so it can be put down in front of the right person.</summary>
    public int? SeatNumber { get; set; }

    /// <summary>
    /// The modifiers, composed into one line of text — "extra cheese, no onion".
    /// </summary>
    /// <remarks>
    /// <b>Composed at firing rather than stored as child rows.</b> A modifier is a child
    /// <see cref="OrderLine"/> in the order model because it carries a price, a tax class and its
    /// own stock; on a ticket it is none of those things — it is a phrase under the item, and the
    /// only consumer is a screen. Rows here would be a second parent/child tree to keep in step
    /// with the first, for a reader that immediately flattens it.
    /// <para>
    /// Null when the item was ordered plain, rather than an empty string, so a display can test
    /// one thing.
    /// </para>
    /// </remarks>
    public string? ModifierText { get; set; }

    /// <summary>A kitchen instruction — "well done", "allergy: nuts".</summary>
    public string? Note { get; set; }
}
