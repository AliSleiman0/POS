using Pos.Core.Exceptions;

namespace Pos.Core.Tenancy;

/// <summary>
/// The per-scope tenant holder. Registered scoped; resolved at most once per scope.
/// </summary>
/// <remarks>
/// Lives in Core, not the API, because three callers need it and none of them should
/// have its own copy: the request pipeline (from the validated token), the narrow
/// pre-authentication paths that must establish a tenant before any token exists
/// (login by slug, refresh token, device token), and tenant provisioning.
/// <para>
/// <b>Resolution is one-way.</b> A second <see cref="Resolve"/> with a different tenant
/// throws instead of switching. Re-pointing a live scope at another tenant mid-request
/// is how a request that started as tenant A ends up writing rows for tenant B, and the
/// entities already tracked by the DbContext would keep their original stamp — a
/// half-switched unit of work is worse than either whole.
/// </para>
/// </remarks>
public sealed class AmbientTenantContext : ITenantContext
{
    private Guid? _tenantId;

    /// <inheritdoc />
    public Guid TenantId => _tenantId ?? throw new TenantNotResolvedException();

    /// <inheritdoc />
    public bool IsResolved => _tenantId.HasValue;

    /// <inheritdoc />
    public bool IsSystemContext => false;

    /// <summary>
    /// Establishes the tenant for this scope. Idempotent for the same tenant; throws for a different one.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is empty.</exception>
    /// <exception cref="InvalidOperationException">A different tenant is already resolved for this scope.</exception>
    public void Resolve(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException(
                "An empty GUID is not a tenant. Resolving one would produce a query filter that " +
                "matches nothing — or matches rows inserted with an empty tenant — without failing.",
                nameof(tenantId));
        }

        if (_tenantId is { } existing && existing != tenantId)
        {
            throw new InvalidOperationException(
                $"Tenant '{existing}' is already resolved for this scope; refusing to switch to '{tenantId}'. " +
                "A unit of work belongs to exactly one tenant. Start a new scope instead.");
        }

        _tenantId = tenantId;
    }
}
