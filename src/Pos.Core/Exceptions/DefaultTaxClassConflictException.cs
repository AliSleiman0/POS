namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when two writers race to make a tax class the tenant's default and the loser hits
/// <c>ux_tax_class_tenant_default</c>.
/// </summary>
/// <remarks>
/// Promoting a tax class is clear-then-set inside a transaction, so the ordinary path never
/// reaches the index. What the transaction does not remove is two concurrent creates that
/// both arrive with <c>isDefault: true</c> and find no default to clear: both insert, and
/// the filtered unique index rejects the second.
/// <para>
/// That is a genuine conflict and it belongs to the caller as a 409, not as a 500. The
/// retry is meaningful — re-reading and promoting will succeed.
/// </para>
/// </remarks>
public sealed class DefaultTaxClassConflictException : PosDomainException
{
    public DefaultTaxClassConflictException()
        : base("Another tax class was made the default at the same time. Re-read and try again.")
    {
    }

    public DefaultTaxClassConflictException(string message)
        : base(message)
    {
    }

    public DefaultTaxClassConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "default-tax-class-conflict";
}
