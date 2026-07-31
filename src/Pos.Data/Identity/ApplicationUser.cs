using Microsoft.AspNetCore.Identity;
using Pos.Core.Tenancy;

namespace Pos.Data.Identity;

/// <summary>
/// A person who can sign in — an Owner, Manager or Cashier at one tenant.
/// </summary>
/// <remarks>
/// Tenant-owned like everything else. That is only possible because login names the
/// tenant (by slug) before it looks for a user; without that, the user table would have to
/// sit outside the query filter and outside RLS, which is the one table where an exception
/// is least affordable.
/// <para>
/// Lives in <c>Pos.Data</c> rather than <c>Pos.Core</c> because
/// <see cref="IdentityUser{TKey}"/> comes from Identity's stores package, and Core
/// references nothing outside the BCL.
/// </para>
/// </remarks>
public sealed class ApplicationUser : IdentityUser<Guid>, ITenantOwned
{
    /// <inheritdoc />
    public Guid TenantId { get; set; }

    /// <summary>Name shown on the PIN screen and on receipts.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// The cashier's PIN, hashed with the same password hasher as
    /// <see cref="IdentityUser{TKey}.PasswordHash"/>.
    /// </summary>
    /// <remarks>
    /// A slow, salted hash, not a bare SHA-256, because a 4-digit PIN has 10,000 possible
    /// values — a fast hash of the whole keyspace is computed in well under a second.
    /// Deliberately different from how device and refresh tokens are hashed: those are
    /// 256-bit random values looked up by hash, which needs a deterministic digest and
    /// gains nothing from a slow one.
    /// <para>
    /// There is no unique index on this column, on purpose. One would let anybody with
    /// write access enumerate which PINs are already taken.
    /// </para>
    /// </remarks>
    public string? PinHash { get; set; }

    /// <summary>
    /// Whether the user may sign in. Users are deactivated, never deleted — their id is
    /// on every sale they rang up.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public DateTimeOffset? LastLoginAt { get; set; }
}
