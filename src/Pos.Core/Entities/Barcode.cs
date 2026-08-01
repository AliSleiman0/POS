using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A code that scans to a product. There are many per product, on purpose.
/// </summary>
/// <remarks>
/// Multipacks, re-labelled stock, supplier variations and the same item arriving with two
/// different codes are all ordinary retail. <b>One barcode per product is the most common
/// retail catalog modelling mistake</b>, and it is discovered only once there is data,
/// when the fix is a migration and a re-label rather than a schema decision.
/// <para>
/// Unlike products, barcodes <i>may</i> be deleted: a mis-scanned label is data entry, not
/// history. Nothing financial points at a barcode — a sale line points at the product.
/// </para>
/// </remarks>
public sealed class Barcode : TenantEntity
{
    /// <summary>
    /// EAN-13 is 13 characters and GS1-128 runs longer; 64 bounds a runaway scanner or a
    /// paste without inventing a limit a real symbology would hit.
    /// </summary>
    public const int CodeMaxLength = 64;

    public Guid ProductId { get; set; }

    /// <summary>The scanned value, unique per tenant. The hottest lookup in the system.</summary>
    public required string Code { get; set; }

    /// <summary>
    /// The code shown when the product is displayed or printed on a label. Advisory: the
    /// register scans whichever code is on the item in the customer's hand.
    /// </summary>
    public bool IsPrimary { get; set; }
}
