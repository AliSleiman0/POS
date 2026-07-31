namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when the current tenant is read before one has been resolved.
/// </summary>
/// <remarks>
/// This exists so that "no tenant" is a loud failure rather than a quiet one.
/// The tempting alternative — returning <see cref="Guid.Empty"/> — produces a
/// query filter of <c>tenant_id = '00000000-…'</c>, which matches nothing on a
/// good day and matches whatever someone inserted with an empty tenant on a bad
/// one. Neither failure announces itself; both look like "no data".
/// </remarks>
public sealed class TenantNotResolvedException : PosDomainException
{
    private const string DefaultMessage =
        "No tenant is resolved for the current context. A tenant is established from the " +
        "validated token (or explicitly, for provisioning and background work) before any " +
        "tenant-owned data is touched.";

    public TenantNotResolvedException()
        : base(DefaultMessage)
    {
    }

    public TenantNotResolvedException(string message)
        : base(message)
    {
    }

    public TenantNotResolvedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "tenant-not-resolved";
}
