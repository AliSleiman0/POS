namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a shop tries to go back to being a retail counter with tables still being served.
/// </summary>
/// <remarks>
/// <b>The one thing a service mode is guarded against, and it is not what <c>TaxMode</c> is
/// guarded against.</b> Nothing historical is at risk here — a sale written in restaurant mode
/// reads identically afterwards, because it is the same row. What is at risk is work in progress:
/// the floor screen is the only way to reach an open order, and every order route answers
/// <see cref="RestaurantModeRequiredException"/> the moment the switch is saved. The bills would
/// still be owed and there would be no way in the product to settle them.
/// <para>
/// A 409 with a real next move, unlike a locked tax mode: settle or abandon the tables, then
/// switch. Stated that way rather than as a permanent refusal, because it is temporary and
/// pretending otherwise sends somebody looking for an override.
/// </para>
/// </remarks>
public sealed class OrdersStillOpenException : PosDomainException
{
    public OrdersStillOpenException()
        : base("There are still orders open. Settle or abandon them before switching back to a counter.")
    {
    }

    public OrdersStillOpenException(string message)
        : base(message)
    {
    }

    public OrdersStillOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "orders-still-open";
}
