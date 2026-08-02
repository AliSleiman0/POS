namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a known idempotency key arrives with a request that is not the one it was
/// issued for.
/// </summary>
/// <remarks>
/// A 409, and it is worth surfacing loudly rather than absorbing. The contract is that a
/// client generates a GUID <i>before its first attempt</i> and reuses it only for retries of
/// that attempt; a key that comes back with different content means the client is recycling
/// keys, and the next thing that happens is a customer being charged for someone else's
/// basket. Answering with the stored response instead would hide it — the till would show a
/// sale that succeeded, for the wrong cart.
/// </remarks>
public sealed class IdempotencyKeyReusedException : PosDomainException
{
    public IdempotencyKeyReusedException()
        : base("That idempotency key has already been used for a different request.")
    {
    }

    public IdempotencyKeyReusedException(string message)
        : base(message)
    {
    }

    public IdempotencyKeyReusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "idempotency-key-reused";
}
