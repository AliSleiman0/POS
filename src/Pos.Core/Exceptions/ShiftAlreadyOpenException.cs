namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a register already has an open shift.
/// </summary>
/// <remarks>
/// Raised from a caught unique-index violation, never from a pre-check: two tills opening at
/// the same moment both pass "is one already open?" and both insert, which would leave a
/// register with two open drawers and no way to say which one a sale belonged to. The filtered
/// index <c>ux_shift_tenant_register_open</c> is the authority; this reports its answer.
/// <para>
/// A 409 because re-reading is a meaningful next step: the caller wants the shift that is
/// already open, and <c>GET /shifts/current</c> returns it.
/// </para>
/// </remarks>
public sealed class ShiftAlreadyOpenException : PosDomainException
{
    public ShiftAlreadyOpenException()
        : base("That register already has an open shift.")
    {
    }

    public ShiftAlreadyOpenException(string message)
        : base(message)
    {
    }

    public ShiftAlreadyOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "shift-already-open";
}
