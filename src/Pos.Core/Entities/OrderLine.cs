using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One item on an order, holding the <b>snapshots</b> a bill will later be priced from.
/// </summary>
/// <remarks>
/// <b>It stores inputs, not amounts.</b> There is no <c>LineSubtotal</c>, <c>LineTax</c> or
/// <c>LineTotal</c> here, unlike <see cref="SaleLine"/>, and their absence is deliberate: the
/// money is computed by <c>Pos.Core.Pricing</c> whenever a bill is quoted or settled, from
/// exactly these fields. Storing totals as well would be a second set of numbers that has to be
/// kept in step through every edit, every void and every re-split — and the first time one drifted
/// the till would show a total the sale would not charge.
/// <para>
/// <b>The snapshots are taken when the item is ordered, not when the bill is paid.</b> A guest
/// who ordered at 18:00 pays the 18:00 price even if the menu changes at 19:00; that is the
/// defensible outcome and it is the same rule CLAUDE.md invariant 5 states for a sale line. The
/// two sets of snapshots agree because the bill copies these forward rather than re-reading the
/// catalog.
/// </para>
/// </remarks>
public sealed class OrderLine : TenantEntity
{
    public const int DescriptionMaxLength = 200;
    public const int VoidReasonMaxLength = 200;
    public const int NoteMaxLength = 200;

    public Guid OrderId { get; set; }

    /// <summary>The product ordered. Never joined to for a price or a name — see the remarks.</summary>
    public Guid ProductId { get; set; }

    /// <summary>1-based, in the order they were keyed. Stable: a void does not renumber.</summary>
    public int LineNumber { get; set; }

    /// <summary>
    /// The line this one modifies — "extra cheese" pointing at the burger it is on.
    /// </summary>
    /// <remarks>
    /// A modifier is an ordinary product on an ordinary line, so it carries its own price, its
    /// own tax class and its own stock behaviour, and it prices through the same engine as
    /// everything else. This column is what nests it under its parent on the screen, on the
    /// kitchen ticket and on the receipt — and what makes "remove the burger" take its
    /// modifiers with it.
    /// <para>
    /// One level deep, by rule: a modifier of a modifier is a menu that needs rethinking, not a
    /// data structure that needs recursion.
    /// </para>
    /// </remarks>
    public Guid? ParentOrderLineId { get; set; }

    /// <summary>The product's name as of ordering. A snapshot: products get renamed.</summary>
    public required string Description { get; set; }

    /// <summary>How many, or how much. Positive — a return is a refund against the sale, not a line here.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The price to charge, which may be a manager's override. A snapshot.</summary>
    public Money UnitPrice { get; set; }

    /// <summary>The tax class's rate as of ordering, as a fraction. A snapshot — rates change by law.</summary>
    public decimal TaxRate { get; set; }

    /// <summary>An absolute amount off this line, typed by a person who held <c>CanApplyDiscount</c>.</summary>
    public Money DiscountAmount { get; set; }

    /// <summary>Whether <see cref="UnitPrice"/> was overridden rather than taken from the catalog.</summary>
    public bool IsPriceOverridden { get; set; }

    /// <summary>Who authorised the override, when it was not the person ordering.</summary>
    public Guid? OverriddenBy { get; set; }

    /// <summary>
    /// Which round this goes to the kitchen in. 1-based; starters are 1.
    /// </summary>
    /// <remarks>
    /// An <see cref="int"/> rather than an enum, because "how many courses" is a property of a
    /// menu and not of this software — a tasting menu has nine and a burger bar has one, and an
    /// enum would force every kitchen into whichever list somebody typed here.
    /// </remarks>
    public int Course { get; set; } = 1;

    /// <summary>
    /// Which seat ordered it, for splitting by seat and for serving without asking "who had the fish?".
    /// </summary>
    /// <remarks>
    /// Nullable and expected to be null often: plenty of shops never key seats, and a mandatory
    /// number would be invented at the till and then relied on by a split that got it wrong.
    /// </remarks>
    public int? SeatNumber { get; set; }

    /// <summary>A kitchen instruction — "no ice", "well done".</summary>
    public string? Note { get; set; }

    /// <inheritdoc cref="Entities.OrderLineStatus" />
    public OrderLineStatus Status { get; set; } = OrderLineStatus.Pending;

    /// <summary>When it was first sent to a station. Null while it is still <see cref="OrderLineStatus.Pending"/>.</summary>
    public DateTimeOffset? FiredAt { get; set; }

    public DateTimeOffset? VoidedAt { get; set; }

    public Guid? VoidedBy { get; set; }

    /// <summary>Required when voiding a line that was already fired: that is food the shop has lost.</summary>
    public string? VoidReason { get; set; }
}
