namespace Pos.Core.Entities;

/// <summary>How a tender was paid.</summary>
/// <remarks>
/// <b>The MVP implements <see cref="Cash"/> only.</b> The others are declared because
/// <c>Tender</c> is a collection of rows carrying this discriminator rather than an amount
/// column on <c>Sale</c> — which is what keeps a future payment method additive instead of a
/// schema migration on the financial table. Nothing is built against them, and per
/// DECISIONS.md card processing is out of scope for the product rather than deferred.
/// <para>
/// Declaring a value is not the same as supporting it: the endpoint accepts <see cref="Cash"/>
/// and refuses the rest, so an unimplemented method cannot arrive through the API and sit in
/// the takings unnoticed.
/// </para>
/// </remarks>
public enum TenderMethod
{
    /// <summary>Notes and coins. The only method the MVP accepts.</summary>
    Cash = 0,

    /// <summary>
    /// A standalone card terminal, with staff keying the amount in. Reconciled in the
    /// Z-report against the terminal's own settlement; the POS never talks to it.
    /// </summary>
    External = 1,

    /// <summary>An integrated card payment. Not implemented, and not planned.</summary>
    Card = 2,

    /// <summary>A voucher or gift card. Beyond the MVP.</summary>
    Voucher = 3,
}
