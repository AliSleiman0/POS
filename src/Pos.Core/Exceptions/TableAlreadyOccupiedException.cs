namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a table already has an open order.
/// </summary>
/// <remarks>
/// Raised from a caught unique-index violation, never from a pre-check — the same reasoning
/// <see cref="ShiftAlreadyOpenException"/> sets out. Two staff seating the same table in the same
/// second both pass "is anything open here?" and both insert, and the result is a table carrying
/// two bills: the next round of drinks joins whichever one the query happened to return, and
/// nobody notices until one is paid and the other is not. The filtered index
/// <c>ux_customer_order_tenant_table_open</c> is the authority; this reports its answer.
/// <para>
/// A 409 because re-reading is the meaningful next step: whoever tried to seat the table wants
/// the order that is already on it, and <c>GET /orders</c> returns it.
/// </para>
/// </remarks>
public sealed class TableAlreadyOccupiedException : PosDomainException
{
    public TableAlreadyOccupiedException()
        : base("That table already has an open order.")
    {
    }

    public TableAlreadyOccupiedException(string message)
        : base(message)
    {
    }

    public TableAlreadyOccupiedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "table-already-occupied";
}
