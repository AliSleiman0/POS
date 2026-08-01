namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a product write would land a SKU that another product in the same tenant
/// already holds.
/// </summary>
/// <remarks>
/// A conflict, not a validation failure, and the distinction decides the status code. The
/// body is well-formed and would be accepted tomorrow if the other product were renamed or
/// deactivated — nothing about the request itself is wrong. So this maps to 409 with a
/// stable <c>type</c>, not to 400 with a per-field <c>errors</c> map.
/// <para>
/// Raised from the unique-violation on <c>ux_product_tenant_sku</c> rather than from a
/// pre-flight lookup. A "does this SKU exist yet?" check is check-then-act: two concurrent
/// creates both pass it and one still hits the index, so a pre-check does not remove the
/// case, it only makes it rare enough to reach production instead of a test.
/// </para>
/// <para>
/// The uniqueness is per tenant, because the index leads with <c>tenant_id</c>. Two shops
/// using the same SKU is ordinary and must keep working.
/// </para>
/// </remarks>
public sealed class DuplicateSkuException : PosDomainException
{
    public DuplicateSkuException()
        : base("Another product in this tenant already uses that SKU.")
    {
    }

    public DuplicateSkuException(string message)
        : base(message)
    {
    }

    public DuplicateSkuException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private DuplicateSkuException(string message, string sku, Exception innerException)
        : base(message, innerException) =>
        Sku = sku;

    /// <summary>
    /// The usual way to raise this: from the unique violation the database reported.
    /// </summary>
    /// <remarks>
    /// A factory rather than a constructor because <c>(string, Exception)</c> is already
    /// taken by the conventional <c>(message, innerException)</c> overload, and having the
    /// same signature mean "message" in one place and "SKU" in another is exactly the sort
    /// of thing that gets a raw SKU into a log line as if it were prose.
    /// </remarks>
    public static DuplicateSkuException ForSku(string sku, Exception innerException) =>
        new($"Another product already uses the SKU '{sku}'.", sku, innerException);

    /// <summary>The normalised SKU that collided, as it would have been stored.</summary>
    public string? Sku { get; }

    public override string ErrorType => "duplicate-sku";
}
