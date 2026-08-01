namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when the tenders offered do not cover the sale.
/// </summary>
/// <remarks>
/// A 409, not a 400. The body is well-formed and would be accepted unchanged if the customer
/// put one more note on the counter — nothing about the request is malformed, so there is no
/// field to point an <c>errors</c> map at. Same shape as <c>DuplicateSkuException</c>.
/// <para>
/// Refused rather than recorded as a part payment. A sale that is half paid for is not a state
/// this system has: the goods leave the shop or they do not, and an unpaid balance is a debt
/// the POS has no way to collect or report on. The register's answer is to take more money,
/// not to record less.
/// </para>
/// </remarks>
public sealed class UnderTenderException : PosDomainException
{
    public UnderTenderException()
        : base("The tenders offered do not cover the sale total.")
    {
    }

    public UnderTenderException(string message)
        : base(message)
    {
    }

    public UnderTenderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "under-tender";
}
