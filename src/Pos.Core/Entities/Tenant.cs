using System.Text.RegularExpressions;

namespace Pos.Core.Entities;

/// <summary>
/// A customer business. Deliberately <b>not</b> a tenant-owned entity — this table is the
/// list of tenants, so it carries no <c>TenantId</c>, no query filter and no RLS policy.
/// </summary>
/// <remarks>
/// That exemption is what makes login work: <c>POST /auth/login</c> resolves this row by
/// <see cref="Slug"/> <i>before</i> any tenant is known, and every table touched after
/// that point is fully scoped. Without a tenant-free entry point the isolation layers
/// would have to be switched off for the user, refresh-token and register tables — the
/// three an attacker would most like to read across tenants.
/// </remarks>
public sealed partial class Tenant
{
    /// <summary>Maximum length of <see cref="Slug"/>; also the check constraint in the database.</summary>
    public const int SlugMaxLength = 63;

    public Guid Id { get; set; }

    public required string Name { get; set; }

    /// <summary>
    /// URL-safe identifier the client presents at login, unique across the platform.
    /// Lowercase, alphanumeric and hyphens.
    /// </summary>
    /// <remarks>
    /// Public by nature — it is typed into a login form and will end up in a subdomain.
    /// It is a <i>selector</i>, never a credential: knowing a slug grants nothing, because
    /// the password still has to match a user inside that tenant.
    /// </remarks>
    public required string Slug { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>ISO 4217, e.g. <c>EUR</c>. Display and rounding only; there is no FX in this system.</summary>
    public required string CurrencyCode { get; set; }

    /// <summary>IANA time zone, e.g. <c>Europe/Dublin</c>.</summary>
    public required string TimeZoneId { get; set; }

    /// <inheritdoc cref="Entities.TaxMode" />
    public TaxMode TaxMode { get; set; } = TaxMode.Inclusive;

    /// <summary>
    /// How far past midnight this tenant's trading day starts, in local time.
    /// </summary>
    /// <remarks>
    /// With an offset of 04:00, a shift closed at 02:00 on Tuesday belongs to Monday's
    /// trading day — which is what the staff who worked it, and the Z-report, both expect.
    /// Applied at query and presentation time; storage stays UTC.
    /// </remarks>
    public TimeSpan BusinessDayStartOffset { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    /// Canonicalises user input into the stored slug form, so that <c>"Corner Shop "</c>
    /// and <c>"corner-shop"</c> resolve to the same tenant at login.
    /// </summary>
    /// <returns>The normalised slug, or <see langword="null"/> if nothing valid remains.</returns>
    public static string? NormalizeSlug(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        // Lowercase is the canonical form because slugs live in URLs and subdomains, where
        // case is not reliably preserved. (CA1308 prefers uppercase normalisation; it is
        // aimed at security comparisons, and this value is an identifier, not a credential.)
#pragma warning disable CA1308
        var lowered = candidate.Trim().ToLowerInvariant();
#pragma warning restore CA1308

        var collapsed = NonSlugCharacters().Replace(lowered, "-").Trim('-');

        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length > SlugMaxLength ? collapsed[..SlugMaxLength] : collapsed;
    }

    /// <summary>Runs of anything that is not a lowercase letter or digit.</summary>
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonSlugCharacters();
}
