namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when an owner tries to take their own owner role away.
/// </summary>
/// <remarks>
/// The tenant locking itself out is the failure this prevents, and it is not recoverable by
/// the customer: there is no platform admin tool by decision, so the only remedy is us
/// reaching into their database by hand. Refusing the click costs an owner one extra step —
/// promote somebody, then have them demote you — and refusing it is the whole feature.
/// <para>
/// A 409 rather than a 400: the body is well-formed, names a real user and a real role, and
/// would be accepted from anybody else. What is wrong is the state of the shop, so there is
/// no field to hang an errors map on.
/// </para>
/// </remarks>
public sealed class SelfDemotionException : PosDomainException
{
    public SelfDemotionException()
        : base("You cannot remove your own owner role. Ask another owner to do it.")
    {
    }

    public SelfDemotionException(string message)
        : base(message)
    {
    }

    public SelfDemotionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "self-demotion";
}
