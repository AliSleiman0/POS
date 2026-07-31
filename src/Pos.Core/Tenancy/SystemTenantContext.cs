using Pos.Core.Exceptions;

namespace Pos.Core.Tenancy;

/// <summary>
/// The explicit, opt-in "no tenant" context, for work that legitimately sits outside
/// any tenant: creating the <c>Tenant</c> row itself, and migrations.
/// </summary>
/// <remarks>
/// It is deliberately <b>not</b> a god-mode context. Reading <see cref="TenantId"/> still
/// throws, so any attempt to touch tenant-owned data under it fails loudly rather than
/// quietly reading across every tenant. Provisioning therefore creates the tenant under
/// this context and then resolves an <see cref="AmbientTenantContext"/> to the new tenant
/// for everything that follows — the ordinary, filtered path.
/// <para>
/// The reason this type exists at all is to make "no tenant" a choice someone typed,
/// rather than the default an unpopulated context falls into.
/// </para>
/// </remarks>
public sealed class SystemTenantContext : ITenantContext
{
    /// <inheritdoc />
    public Guid TenantId => throw new TenantNotResolvedException(
        "The system context owns no tenant. Create the tenant, then resolve an " +
        $"{nameof(AmbientTenantContext)} to it for any tenant-owned work.");

    /// <inheritdoc />
    public bool IsResolved => false;

    /// <inheritdoc />
    public bool IsSystemContext => true;
}
