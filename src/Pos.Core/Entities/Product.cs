using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// Something a shop sells. The row every sale line points back at.
/// </summary>
/// <remarks>
/// <b>Never hard-deleted.</b> Sale lines reference products permanently, so a delete either
/// orphans history or cascades a customer's sales away. Withdrawal is
/// <see cref="IsActive"/> — the product stops being sellable and stays readable.
/// <para>
/// The price here is the <i>current</i> price and is only ever used to price a <i>new</i>
/// sale. A report that joins to it retroactively rewrites past revenue and stops
/// reconciling with the cash that was taken; <c>SaleLine</c> keeps its own snapshot for
/// that reason. See CLAUDE.md invariant 5.
/// </para>
/// </remarks>
public sealed class Product : TenantEntity
{
    public const int SkuMaxLength = 64;
    public const int NameMaxLength = 200;
    public const int DescriptionMaxLength = 1000;

    /// <summary>
    /// The shop's own identifier for the item, unique per tenant. Normalised through
    /// <see cref="NormalizeSku"/> so it is matched exactly.
    /// </summary>
    public required string Sku { get; set; }

    /// <summary>Shown on the register, the receipt and every report.</summary>
    public required string Name { get; set; }

    public string? Description { get; set; }

    /// <summary>Optional: an uncategorised product is still sellable, it is just harder to find.</summary>
    public Guid? CategoryId { get; set; }

    /// <summary>
    /// Required. A product with no tax class cannot be priced, so this is not nullable
    /// even though it means onboarding must create a tax class first.
    /// </summary>
    public Guid TaxClassId { get; set; }

    /// <summary>Current selling price, <c>numeric(19,4)</c>. Interpreted per the tenant's <see cref="TaxMode"/>.</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>
    /// What the shop paid. Drives margin reporting, and is <b>omitted from the response</b>
    /// for callers without <c>CanViewMargins</c> — not hidden client-side, because anything
    /// sent to a browser is readable.
    /// </summary>
    public decimal? CostPrice { get; set; }

    /// <inheritdoc cref="Entities.Unit" />
    public Unit Unit { get; set; } = Unit.Each;

    /// <summary>Soft delete. See the remarks on the class.</summary>
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Whether selling this decrements stock. False for services and open-price items,
    /// which have no inventory to decrement and would otherwise accumulate a meaningless
    /// negative on-hand.
    /// </summary>
    public bool TrackStock { get; set; } = true;

    /// <summary>
    /// Canonicalises staff input into the stored SKU form, so <c>"abc-1 "</c> and
    /// <c>"ABC-1"</c> are the same product rather than two.
    /// </summary>
    /// <remarks>
    /// The uniqueness index is a plain unique index on the stored value, so it is only as
    /// case-insensitive as what is written into it. Normalising on the way in is what makes
    /// "this SKU is taken" true rather than approximately true.
    /// </remarks>
    /// <returns>The normalised SKU, or <see langword="null"/> if nothing usable remains.</returns>
    public static string? NormalizeSku(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        // Uppercase: SKUs are printed on labels and shelf-edge tickets, where uppercase is
        // the convention, and it is the invariant-culture direction analyzers prefer.
        var normalised = candidate.Trim().ToUpperInvariant();

        return normalised.Length > SkuMaxLength ? normalised[..SkuMaxLength] : normalised;
    }
}
