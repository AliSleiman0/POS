namespace Pos.Core.Exceptions;

/// <summary>
/// More was requested than remains refundable on that line.
/// </summary>
/// <remarks>
/// Computed with the original sale locked, so two concurrent refunds cannot both pass a check that each of them, alone, would have passed. Without the lock a customer could return two of one item twice and be paid for four.
/// </remarks>
public sealed class RefundExceedsOriginalException : PosDomainException
{
    public RefundExceedsOriginalException()
        : base("More was requested than remains refundable on that line.")
    {
    }

    public RefundExceedsOriginalException(string message)
        : base(message)
    {
    }

    public RefundExceedsOriginalException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "refund-exceeds-original";
}
