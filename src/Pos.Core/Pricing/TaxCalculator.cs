using Pos.Core.Entities;
using Pos.Core.Monetary;

namespace Pos.Core.Pricing;

/// <summary>
/// Tax on one line's discounted amount, in whichever direction the tenant prices.
/// </summary>
/// <remarks>
/// The two modes are not two ways of displaying one number — they are two different meanings
/// for the same stored price, which is why <c>TaxMode</c> is fixed at onboarding and refused
/// afterwards. <c>Exclusive</c>: the shelf says €10 and the customer pays €12.30. <c>Inclusive</c>:
/// the shelf says €10 and the customer pays €10, of which €1.87 is tax.
/// <para>
/// Nothing here rounds. Every value comes back at full precision and the pipeline rounds once,
/// at the header.
/// </para>
/// </remarks>
public static class TaxCalculator
{
    /// <summary>
    /// Tax contained in, or to be added to, <paramref name="taxable"/>.
    /// </summary>
    /// <param name="taxable">The amount after every discount. Tax is never on the gross.</param>
    /// <param name="rate">The rate as a fraction: <c>0.2300</c> is 23%.</param>
    /// <param name="mode">How <paramref name="taxable"/> should be read.</param>
    public static Money TaxOn(Money taxable, decimal rate, TaxMode mode) => mode switch
    {
        // Added on top: the price did not contain it.
        TaxMode.Exclusive => taxable * rate,

        // Extracted from within: gross × rate / (1 + rate). At a rate of 1 — legal, if
        // degenerate — the divisor is 2 and exactly half the shelf price is tax, which is
        // the right answer rather than an edge case to guard.
        TaxMode.Inclusive => taxable * (rate / (1m + rate)),

        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tax mode."),
    };

    /// <summary>
    /// The net-of-tax value of <paramref name="amount"/> — what it contributes to
    /// <c>Sale.Subtotal</c>.
    /// </summary>
    /// <remarks>
    /// In <c>Exclusive</c> mode a stored price is already net, so this is the identity. In
    /// <c>Inclusive</c> mode it has to be divided out, which is the whole reason
    /// <c>Sale.Subtotal</c> cannot simply be the sum of quantity times price.
    /// </remarks>
    public static Money NetOfTax(Money amount, decimal rate, TaxMode mode) => mode switch
    {
        TaxMode.Exclusive => amount,
        TaxMode.Inclusive => amount / (1m + rate),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown tax mode."),
    };
}
