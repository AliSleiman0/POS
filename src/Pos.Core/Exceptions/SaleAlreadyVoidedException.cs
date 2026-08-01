namespace Pos.Core.Exceptions;

/// <summary>
/// That sale has already been voided.
/// </summary>
/// <remarks>
/// A 409 rather than a 404 or a silent success. The sale exists and the caller can see it; what has changed is that somebody voided it already, and re-voiding would write a second set of compensating stock movements and put the goods back twice.
/// </remarks>
public sealed class SaleAlreadyVoidedException : PosDomainException
{
    public SaleAlreadyVoidedException()
        : base("That sale has already been voided.")
    {
    }

    public SaleAlreadyVoidedException(string message)
        : base(message)
    {
    }

    public SaleAlreadyVoidedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "sale-already-voided";
}
