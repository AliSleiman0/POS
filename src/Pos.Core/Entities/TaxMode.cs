namespace Pos.Core.Entities;

/// <summary>
/// How a tenant's stored prices relate to tax.
/// </summary>
/// <remarks>
/// <b>Set at onboarding and effectively immutable.</b> It does not change how a price is
/// displayed — it changes what a stored price <i>means</i>. Flipping it after trading
/// reinterprets every price already recorded and silently rewrites history, so
/// <c>PUT /settings</c> refuses the change once sales exist rather than warning about it.
/// See DECISIONS.md, "Two decisions that are not retrofittable".
/// </remarks>
public enum TaxMode
{
    /// <summary>Shelf prices include tax; tax is extracted from the line total. EU-style retail.</summary>
    Inclusive = 0,

    /// <summary>Shelf prices exclude tax; tax is added at the till. US-style retail.</summary>
    Exclusive = 1,
}
