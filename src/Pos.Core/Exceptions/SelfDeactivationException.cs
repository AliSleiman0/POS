namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a user tries to deactivate themselves.
/// </summary>
/// <remarks>
/// The same lock-out as <see cref="SelfDemotionException"/>, reached by the other door.
/// Applied to every role rather than to owners alone: a cashier who deactivates themselves
/// mid-shift cannot undo it either, and nothing is lost by making "remove this person" always
/// someone else's action.
/// <para>
/// A 409, on the same reasoning as <see cref="SelfDemotionException"/>.
/// </para>
/// </remarks>
public sealed class SelfDeactivationException : PosDomainException
{
    public SelfDeactivationException()
        : base("You cannot deactivate yourself. Ask another owner to do it.")
    {
    }

    public SelfDeactivationException(string message)
        : base(message)
    {
    }

    public SelfDeactivationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "self-deactivation";
}
