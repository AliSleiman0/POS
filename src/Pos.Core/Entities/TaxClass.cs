using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A named tax rate that products point at, rather than a rate stored on each product.
/// </summary>
/// <remarks>
/// Tax rates change by legislation. A rate held on every product turns a VAT change into a
/// mass update across the catalog — slow, and wrong halfway through if it fails. A class
/// makes it one row.
/// <para>
/// Historical sales are unaffected either way: <c>SaleLine</c> snapshots the rate as of the
/// sale, so changing this row never rewrites what a customer was charged. That is the
/// property that lets a rate be edited at all.
/// </para>
/// </remarks>
public sealed class TaxClass : TenantEntity
{
    public const int NameMaxLength = 60;

    /// <summary>
    /// <c>numeric(6,4)</c> — deliberately narrower than the <c>(19,4)</c> every other
    /// decimal gets. A tax rate is a fraction, not an amount of money, and the width is
    /// what stops a fat-fingered 20 (2,000%) being storable.
    /// </summary>
    public const int RatePrecision = 6;

    /// <inheritdoc cref="RatePrecision" />
    public const int RateScale = 4;

    public required string Name { get; set; }

    /// <summary>The rate as a fraction: <c>0.2000</c> is 20%. Never a percentage figure.</summary>
    public decimal Rate { get; set; }

    /// <summary>
    /// The rate applied to a new product when none is chosen. At most one per tenant,
    /// enforced by a filtered unique index — two defaults would price new items
    /// nondeterministically depending on which row was read first.
    /// </summary>
    public bool IsDefault { get; set; }
}
