namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a barcode write would land a code that another barcode in the same tenant
/// already holds — whether on the same product or a different one.
/// </summary>
/// <remarks>
/// A conflict, not a validation failure, for the same reason as
/// <see cref="DuplicateSkuException"/>: the request is well-formed and would be accepted
/// once the other barcode is removed. So it maps to 409 with a stable <c>type</c>, not to
/// 400 with a per-field <c>errors</c> map.
/// <para>
/// Raised from the unique violation on <c>ux_barcode_tenant_code</c>, never from a
/// pre-flight lookup. A code is unique per tenant because the index leads with
/// <c>tenant_id</c> — two shops stocking the same manufacturer's item hold the same code
/// and must both keep working, which is precisely what the isolation tests assert.
/// </para>
/// <para>
/// The collision is reported the same way whichever product owns the other barcode. Saying
/// "that code is already on product X" would be more helpful, and would also answer "does
/// this code exist in this tenant, and on what?" to a caller who may not be entitled to
/// know — the catalog screen can look it up through the endpoints it already has.
/// </para>
/// </remarks>
public sealed class DuplicateBarcodeException : PosDomainException
{
    public DuplicateBarcodeException()
        : base("Another product in this tenant already uses that barcode.")
    {
    }

    public DuplicateBarcodeException(string message)
        : base(message)
    {
    }

    public DuplicateBarcodeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    private DuplicateBarcodeException(string message, string code, Exception innerException)
        : base(message, innerException) =>
        Code = code;

    /// <summary>The usual way to raise this: from the unique violation the database reported.</summary>
    /// <remarks>
    /// A factory rather than a constructor because <c>(string, Exception)</c> already means
    /// <c>(message, innerException)</c>. See <see cref="DuplicateSkuException.ForSku"/>.
    /// </remarks>
    public static DuplicateBarcodeException ForCode(string code, Exception innerException) =>
        new($"Another product already uses the barcode '{code}'.", code, innerException);

    /// <summary>The normalised code that collided, as it would have been stored.</summary>
    public string? Code { get; }

    public override string ErrorType => "duplicate-barcode";
}
