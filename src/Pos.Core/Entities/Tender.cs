using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One payment against a sale. A <b>collection</b>, not a column.
/// </summary>
/// <remarks>
/// Split payments are ordinary retail — a customer pays €20 in cash and the rest from a
/// voucher — so an amount column on <c>Sale</c> could not represent them. The
/// <see cref="Method"/> discriminator is also what keeps a future payment method additive
/// rather than a migration on the financial table.
/// <para>
/// <c>sum(Amount) &gt;= Sale.Total</c> for a sale, with the excess given back as
/// <see cref="ChangeGiven"/>; <c>sum(Amount) == Sale.Total</c> for a refund, where both are
/// negative and no change is given. That is DATA-MODEL.md invariant 2.
/// </para>
/// </remarks>
public sealed class Tender : TenantEntity
{
    public const int ReferenceMaxLength = 100;

    public Guid SaleId { get; set; }

    /// <inheritdoc cref="TenderMethod" />
    public TenderMethod Method { get; set; } = TenderMethod.Cash;

    /// <summary>
    /// What was handed over — the €20 note, not the €18.45 owed. Negative on a refund.
    /// </summary>
    /// <remarks>
    /// Storing the tendered amount rather than the applied amount is what makes the drawer
    /// reconcilable: the till really did receive €20, and <see cref="ChangeGiven"/> really
    /// did leave it.
    /// </remarks>
    public Money Amount { get; set; }

    /// <summary>Change handed back out of this tender. Null when none was given.</summary>
    public Money? ChangeGiven { get; set; }

    /// <summary>A terminal's reference for an <see cref="TenderMethod.External"/> payment.</summary>
    public string? Reference { get; set; }
}
