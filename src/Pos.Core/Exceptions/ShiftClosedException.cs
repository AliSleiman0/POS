namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a sale, refund or cash movement names a shift that is not open.
/// </summary>
/// <remarks>
/// A 409 rather than a 400 on the field, and the distinction is what the cashier does next.
/// An unknown shift id is a client bug — a 400 saying "no such shift". A <i>closed</i> shift
/// is a real shift that the register is simply no longer trading on, and the answer is "open
/// one", which is an action rather than a correction.
/// <para>
/// It is also the losing side of the race a shift close deliberately creates: the close takes
/// an exclusive lock on the row, so a sale arriving mid-close either commits before the close
/// counts it or blocks, finds it closed, and is refused. Never counted-then-refused, and never
/// committed-but-uncounted.
/// </para>
/// </remarks>
public sealed class ShiftClosedException : PosDomainException
{
    public ShiftClosedException()
        : base("That shift is closed. Open a new one before selling.")
    {
    }

    public ShiftClosedException(string message)
        : base(message)
    {
    }

    public ShiftClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "shift-closed";
}
