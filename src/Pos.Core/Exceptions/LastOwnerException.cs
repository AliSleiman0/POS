namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a change would leave a tenant with no active owner.
/// </summary>
/// <remarks>
/// The guardrail that catches what <see cref="SelfDemotionException"/> and
/// <see cref="SelfDeactivationException"/> cannot: two owners, each deactivating the other,
/// or one owner demoting the last of their colleagues. Nobody is acting on themselves, and
/// the shop still ends up with nobody who can manage staff.
/// <para>
/// Raised inside a transaction that holds a row lock over the owners it counted, so two
/// concurrent removals cannot both read "there are two of us" and both proceed. A count taken
/// outside a lock would be right at the instant it was taken and wrong by the time it was
/// acted on, which is precisely the case that empties the shop.
/// </para>
/// <para>
/// A 409: the request is well-formed, and the caller has a real next move — promote somebody
/// first.
/// </para>
/// </remarks>
public sealed class LastOwnerException : PosDomainException
{
    public LastOwnerException()
        : base("A shop must keep at least one active owner. Give somebody else the owner role first.")
    {
    }

    public LastOwnerException(string message)
        : base(message)
    {
    }

    public LastOwnerException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "last-owner";
}
