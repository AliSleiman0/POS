namespace Pos.Core.Tenancy;

/// <summary>
/// Marks a row as belonging to exactly one tenant. Implementing this is the whole
/// registration a new entity needs: the query filter and the write interceptor are
/// applied to every implementor by reflection.
/// </summary>
/// <remarks>
/// An interface rather than only the <see cref="TenantEntity"/> base class, because
/// <c>ApplicationUser</c> must inherit <c>IdentityUser&lt;Guid&gt;</c> and C# has single
/// inheritance. A base-class-only rule would leave the user table — the one table an
/// attacker most wants to read across tenants — silently outside the mechanism.
/// </remarks>
public interface ITenantOwned
{
    /// <summary>
    /// The owning tenant. Stamped by the SaveChanges interceptor on insert; never set
    /// by callers, and never taken from a route, query string or request body.
    /// </summary>
    Guid TenantId { get; set; }
}
