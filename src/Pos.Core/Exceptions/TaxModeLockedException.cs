namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a shop tries to change its tax mode after it has started trading.
/// </summary>
/// <remarks>
/// <c>TaxMode</c> decides whether a stored price <i>includes</i> tax or has it added, so
/// changing it does not reinterpret future sales — it reinterprets every price already
/// recorded. Every historical total silently becomes a different number, reports stop
/// reconciling with the cash that was taken, and nothing in the data says when the meaning
/// changed. Each sale snapshots its own mode precisely so this cannot happen retroactively;
/// this refusal is what stops the tenant row disagreeing with them.
/// <para>
/// Refused rather than warned about, per docs/API.md. A warning on a screen is dismissed by
/// somebody who does not know what it means, and the damage is not detectable afterwards.
/// </para>
/// <para>
/// A 409: the body is well-formed and would have been accepted before the shop's first sale.
/// What is wrong is the state of the world.
/// </para>
/// </remarks>
public sealed class TaxModeLockedException : PosDomainException
{
    public TaxModeLockedException()
        : base("Tax mode cannot be changed once the shop has recorded a sale.")
    {
    }

    public TaxModeLockedException(string message)
        : base(message)
    {
    }

    public TaxModeLockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "tax-mode-locked";
}
