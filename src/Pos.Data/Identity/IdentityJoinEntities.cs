using Microsoft.AspNetCore.Identity;
using Pos.Core.Tenancy;

namespace Pos.Data.Identity;

/*
 * Identity's satellite tables, subclassed only to add TenantId.
 *
 * Left as the stock types they would be the only tables in the schema outside the tenant
 * mechanism — no query filter, no RLS policy — and one of them, user_token, is where
 * password-reset and 2FA tokens live. Reading another tenant's row there is not an
 * information leak, it is an account takeover.
 *
 * Adding the column is cheap. Leaving a documented exception in the one phase whose whole
 * purpose is "no table is outside the mechanism" is not.
 */

/// <summary>Links a user to one of the platform roles.</summary>
public sealed class ApplicationUserRole : IdentityUserRole<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>Per-user claims. Unused today; Identity requires the table.</summary>
public sealed class ApplicationUserClaim : IdentityUserClaim<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>External login providers. Unused — auth is self-hosted, per DECISIONS.md.</summary>
public sealed class ApplicationUserLogin : IdentityUserLogin<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// Identity-issued tokens (password reset, 2FA). The table most worth scoping.
/// </summary>
public sealed class ApplicationUserToken : IdentityUserToken<Guid>, ITenantOwned
{
    public Guid TenantId { get; set; }
}

/// <summary>
/// Claims attached to a role. Not tenant-owned, for the same reason
/// <see cref="ApplicationRole"/> is not: the rows describe the platform's fixed roles.
/// </summary>
public sealed class ApplicationRoleClaim : IdentityRoleClaim<Guid>;
