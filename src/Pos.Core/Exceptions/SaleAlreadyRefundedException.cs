namespace Pos.Core.Exceptions;

/// <summary>
/// That sale has a refund against it and can no longer be voided.
/// </summary>
/// <remarks>
/// Voiding a sale writes compensating movements for every line, and a refund has already returned some of them. Doing both would put the same goods back twice and leave the shop's stock overstated by exactly the amount that was returned. The correct action is to refund the remainder, or to void the refund first.
/// </remarks>
public sealed class SaleAlreadyRefundedException : PosDomainException
{
    public SaleAlreadyRefundedException()
        : base("That sale has a refund against it and can no longer be voided.")
    {
    }

    public SaleAlreadyRefundedException(string message)
        : base(message)
    {
    }

    public SaleAlreadyRefundedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "sale-already-refunded";
}
