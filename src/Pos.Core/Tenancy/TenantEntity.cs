namespace Pos.Core.Tenancy;

/// <summary>
/// Base for every tenant-owned entity. Deriving from this is the entire registration
/// a new entity needs — see <see cref="ITenantOwned"/>.
/// </summary>
/// <remarks>
/// Shape fixed by docs/DATA-MODEL.md#conventions. The audit columns live here rather
/// than on <see cref="ITenantOwned"/> because <c>ApplicationUser</c> is tenant-owned but
/// carries Identity's own column set.
/// <para>
/// Every index on a table deriving from this leads with <c>TenantId</c>: the global
/// query filter guarantees every query filters by tenant, so a leading <c>TenantId</c>
/// is what makes an index usable rather than scanned.
/// </para>
/// </remarks>
public abstract class TenantEntity : ITenantOwned
{
    public Guid Id { get; set; }

    /// <inheritdoc />
    public Guid TenantId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>User who created the row. Null for rows created by provisioning or a background job.</summary>
    public Guid? CreatedBy { get; set; }

    public DateTimeOffset? UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}
