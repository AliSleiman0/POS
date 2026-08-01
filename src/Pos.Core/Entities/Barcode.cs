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
    /// <remarks>
    /// <b>Deliberately unconstrained</b> (decided in Phase 2.3). Nothing enforces one primary
    /// per product: a filtered unique index would make EF treat the foreign-key index as
    /// covered, and would turn "make this the label code" into a clear-then-set across two
    /// saves inside a transaction — real cost, in exchange for a rule nothing reads yet.
    /// Two primaries on one product is therefore possible and harmless; the screen that
    /// eventually shows a label code picks one and the decision can be revisited then.
    /// </remarks>
    public bool IsPrimary { get; set; }

    /// <summary>
    /// Canonicalises a scanned or typed code into the stored form.
    /// </summary>
    /// <remarks>
    /// Trims and nothing else. <b>No case folding</b>, and the asymmetry with
    /// <see cref="Product.NormalizeSku"/> is deliberate rather than an omission: a SKU is a
    /// human-typed identifier where <c>abc-1</c> and <c>ABC-1</c> must be one product,
    /// whereas a barcode is whatever the symbology encodes. GS1-128 application identifiers
    /// carry case-significant data, so upper-casing a scan would silently change the code
    /// the label actually holds.
    /// </remarks>
    /// <returns>The normalised code, or <see langword="null"/> if nothing usable remains.</returns>
    public static string? NormalizeCode(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        var normalised = candidate.Trim();

        return normalised.Length > CodeMaxLength ? normalised[..CodeMaxLength] : normalised;
    }
}
