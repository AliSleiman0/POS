using Pos.Core.Exceptions;

namespace Pos.Core.Tenancy;

/// <summary>
/// The ambient tenant for the current unit of work.
/// </summary>
/// <remarks>
/// Populated once per request from the <b>validated token only</b>, or explicitly by
/// the narrow pre-authentication paths (login by tenant slug, refresh, device token)
/// and by provisioning. A tenant appearing in a route, query string or request body is
/// never trusted.
/// </remarks>
public interface ITenantContext
{
    /// <summary>
    /// The current tenant.
    /// </summary>
    /// <exception cref="TenantNotResolvedException">
    /// No tenant has been resolved. This throws rather than returning a sentinel — see
    /// <see cref="TenantNotResolvedException"/> for why an empty GUID is worse than an error.
    /// </exception>
    Guid TenantId { get; }

    /// <summary>Whether <see cref="TenantId"/> can be read without throwing.</summary>
    bool IsResolved { get; }

    /// <summary>
    /// True only for the explicit, opt-in system context used by provisioning and
    /// migrations. Never a fallback for "no tenant was resolved" — a background job
    /// with no tenant must not be mistaken for one entitled to every tenant.
    /// </summary>
    bool IsSystemContext { get; }
}
