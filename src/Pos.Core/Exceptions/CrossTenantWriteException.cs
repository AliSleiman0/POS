namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when a unit of work attempts to write a row belonging to a different tenant.
/// </summary>
/// <remarks>
/// The query filter means code normally cannot <i>load</i> another tenant's row to begin
/// with. This catches the paths that get around that: an entity attached by id, a
/// deserialised object carrying a <c>TenantId</c> from the request body, or a row loaded
/// under <c>IgnoreQueryFilters()</c> and then saved.
/// <para>
/// It is a write-side backstop for a read-side guarantee, which is exactly the kind of
/// redundancy this system wants: the two layers fail independently.
/// </para>
/// </remarks>
public sealed class CrossTenantWriteException : PosDomainException
{
    public CrossTenantWriteException()
        : base("A write was attempted against a row owned by another tenant.")
    {
    }

    public CrossTenantWriteException(string message)
        : base(message)
    {
    }

    public CrossTenantWriteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CrossTenantWriteException(string entityType, Guid attemptedTenantId, Guid ambientTenantId)
        : base($"Refusing to write '{entityType}' owned by tenant '{attemptedTenantId}' " +
               $"while the current tenant is '{ambientTenantId}'.")
    {
        AttemptedTenantId = attemptedTenantId;
        AmbientTenantId = ambientTenantId;
    }

    /// <summary>The tenant the offending row claims to belong to.</summary>
    public Guid? AttemptedTenantId { get; }

    /// <summary>The tenant the current unit of work is scoped to.</summary>
    public Guid? AmbientTenantId { get; }

    public override string ErrorType => "cross-tenant-write";
}
