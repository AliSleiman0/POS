using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// A manager's authorisation for one privileged action on somebody else's session.
/// </summary>
/// <remarks>
/// A cashier discounting a line needs a manager's permission, and the two obvious ways to get
/// it are both wrong. Swapping the session attributes the sale — and the override — to the
/// manager, so the Z-report reconciles the wrong person's till. Handing the cashier the
/// manager's role for a while is a standing grant with no end.
/// <para>
/// This is the third way: the manager's PIN mints a token that satisfies one named policy,
/// once. The cashier's session is untouched, the sale stays theirs, and
/// <see cref="SaleLine.OverriddenBy"/> records who actually authorised it.
/// </para>
/// <para>
/// <b><see cref="ConsumedAt"/> is the control, not <see cref="ExpiresAt"/>.</b> A grant that
/// merely expired after a few minutes would let one PIN entry discount every sale in that
/// window, which is the exact fraud this exists to prevent. The expiry only bounds a grant
/// minted for a sale that was then abandoned.
/// </para>
/// <para>
/// Stored hashed, like <see cref="RefreshToken"/> and a register's device token: a leaked
/// database gives an attacker digests, not usable authorisations.
/// </para>
/// </remarks>
public sealed class OverrideGrant : TenantEntity
{
    /// <summary>Length of the stored SHA-256 digest in base64.</summary>
    public const int TokenHashLength = 44;

    /// <summary>
    /// How long a grant stays usable.
    /// </summary>
    /// <remarks>
    /// Long enough to finish scanning and tender the sale it was minted for; short enough that
    /// a manager who authorised something and walked away has not left one lying about. It is
    /// deliberately not the security boundary — single use is.
    /// </remarks>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>The manager who authorised. Becomes <see cref="SaleLine.OverriddenBy"/>.</summary>
    public Guid UserId { get; set; }

    /// <summary>SHA-256 of the full token string. The token itself is never stored.</summary>
    public required string TokenHash { get; set; }

    /// <summary>
    /// The policies this grant satisfies, from <c>PolicyCatalog</c>.
    /// </summary>
    /// <remarks>
    /// A set rather than one name because a cart can carry both a discount and a price
    /// override, and two grants would mean two headers and two PIN entries for one
    /// authorisation the manager gave once.
    /// </remarks>
    public required string[] Policies { get; set; }

    /// <summary>
    /// The till it was minted at.
    /// </summary>
    /// <remarks>
    /// Checked when it is spent. Without it, a grant minted at the front counter could be
    /// carried to another drawer — and the manager standing at the first till would have no way
    /// to know what they had authorised.
    /// </remarks>
    public Guid RegisterId { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Set inside the transaction that spends it. Its presence means "already used".</summary>
    public DateTimeOffset? ConsumedAt { get; set; }

    /// <summary>The sale it was spent on, so the row explains itself.</summary>
    public Guid? ConsumedBySaleId { get; set; }

    /// <summary>Whether this grant can still authorise anything.</summary>
    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && ExpiresAt > now;

    /// <summary>Whether it covers every policy the request needs.</summary>
    public bool Covers(IEnumerable<string> required)
    {
        ArgumentNullException.ThrowIfNull(required);

        return required.All(policy => Policies.Contains(policy, StringComparer.Ordinal));
    }
}
