namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when an order is asked to change after it has been closed or abandoned.
/// </summary>
/// <remarks>
/// Adding a line to a settled order is the restaurant equivalent of ringing a sale into a closed
/// drawer: the money has been counted, a receipt has been handed over, and the food would be
/// given away with nothing on any row to explain it. The order's status is checked under the same
/// lock the write takes, so a close that lands mid-request wins rather than racing.
/// <para>
/// A 409, because the caller's next move is to look at what the order became — not to retry.
/// </para>
/// </remarks>
public sealed class OrderNotOpenException : PosDomainException
{
    public OrderNotOpenException()
        : base("That order is no longer open.")
    {
    }

    public OrderNotOpenException(string message)
        : base(message)
    {
    }

    public OrderNotOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "order-not-open";
}
