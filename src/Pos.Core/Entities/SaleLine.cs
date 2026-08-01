using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One line of a sale, holding <b>snapshots</b> of everything that could later change.
/// </summary>
/// <remarks>
/// <b>This is the entity CLAUDE.md invariant 5 is about.</b> <see cref="Description"/>,
/// <see cref="UnitPrice"/> and <see cref="TaxRate"/> are copied from the catalog at the moment
/// of sale and never read from it again. A report that joined back to <c>Product.UnitPrice</c>
/// would let a price change on Tuesday retroactively rewrite Monday's revenue — the reports
/// would stop reconciling with the cash that was actually taken, and nothing would surface the
/// discrepancy because both numbers would look reasonable.
/// <para>
/// The money columns here are stored at <c>numeric(19,4)</c> and are <i>not</i> rounded to the
/// payable scale. Only the <see cref="Sale"/> header is, once. See <c>Pos.Core.Monetary.Rounding</c>.
/// </para>
/// </remarks>
public sealed class SaleLine : TenantEntity
{
    public const int DescriptionMaxLength = 200;

    public Guid SaleId { get; set; }

    /// <summary>
    /// The product sold. Kept for reporting and for the stock movement, but <b>never</b>
    /// joined to for an amount or a name.
    /// </summary>
    public Guid ProductId { get; set; }

    /// <summary>1-based, in the order the cashier rang them up. What the receipt prints.</summary>
    public int LineNumber { get; set; }

    /// <summary>The product's name as of the sale. A snapshot: products get renamed.</summary>
    public required string Description { get; set; }

    /// <summary>How many, or how much. Signed: negative on a refund line.</summary>
    public decimal Quantity { get; set; }

    /// <summary>The price charged, which may be an override. A snapshot.</summary>
    public Money UnitPrice { get; set; }

    /// <summary>The tax class's rate as of the sale, as a fraction. A snapshot — rates change by law.</summary>
    public decimal TaxRate { get; set; }

    /// <summary>
    /// This line's discount net of tax, including its apportioned share of any cart discount.
    /// </summary>
    public Money DiscountAmount { get; set; }

    /// <summary>Net of tax, before discount.</summary>
    public Money LineSubtotal { get; set; }

    public Money LineTax { get; set; }

    /// <summary>This line's contribution to what the customer paid.</summary>
    public Money LineTotal { get; set; }

    /// <summary>
    /// Whether the price was overridden rather than taken from the catalog.
    /// </summary>
    /// <remarks>
    /// Recorded on the line because a price override is the single most useful signal in a
    /// shrinkage investigation, and because Phase 7's audit log does not exist yet — this
    /// column and <see cref="OverriddenBy"/> are the whole audit trail for it until then.
    /// </remarks>
    public bool IsPriceOverridden { get; set; }

    /// <summary>Who authorised the override.</summary>
    public Guid? OverriddenBy { get; set; }

    /// <summary>
    /// The line this refund line reverses.
    /// </summary>
    /// <remarks>
    /// Load-bearing and easy to miss: without it, "how much of line 3 is still refundable" is
    /// unanswerable, and the only alternative — matching on <see cref="ProductId"/> — breaks
    /// the moment the same product appears on two lines of one sale, which is ordinary.
    /// </remarks>
    public Guid? OriginalSaleLineId { get; set; }
}
