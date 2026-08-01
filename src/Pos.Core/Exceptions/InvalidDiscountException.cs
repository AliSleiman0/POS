namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a discount is larger than the thing it is discounting.
/// </summary>
/// <remarks>
/// A 400, not a 409: nothing raced. The request asks for €12 off a €10 basket, which is a
/// statement about the body and would be equally wrong tomorrow.
/// <para>
/// The engine refuses rather than clamping to zero. Clamping would turn "I typed 12 instead of
/// 1.20" into a free basket that balances perfectly and looks deliberate in every report —
/// a discount that large is either a typo or theft, and both want a person to see them.
/// A discount that exactly equals the basket is <i>legal</i>: a fully comped sale is real, and
/// it still records what was taken and why.
/// </para>
/// </remarks>
public sealed class InvalidDiscountException : PosDomainException
{
    public InvalidDiscountException()
        : base("A discount cannot be larger than the amount it applies to.")
    {
    }

    public InvalidDiscountException(string message)
        : base(message)
    {
    }

    public InvalidDiscountException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "invalid-discount";
}
