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

    /// <summary>Maximum length of <see cref="AddressLine"/>.</summary>
    public const int AddressLineMaxLength = 500;

    /// <summary>Maximum length of <see cref="TaxNumber"/>.</summary>
    public const int TaxNumberMaxLength = 64;

    /// <summary>
    /// Maximum length of <see cref="ReceiptHeader"/> and <see cref="ReceiptFooter"/>.
    /// </summary>
    /// <remarks>
    /// Generous, because 80mm paper is cheap and a shop's returns policy is not short — but
    /// bounded, because this text is printed on every receipt and an unbounded column is a way
    /// to make a till spool a metre of paper per sale.
    /// </remarks>
    public const int ReceiptTextMaxLength = 1000;

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

    /// <inheritdoc cref="Entities.ServiceMode" />
    public ServiceMode ServiceMode { get; set; } = ServiceMode.Retail;

    /// <summary>
    /// The smallest coin a cash total is rounded to, e.g. <c>0.05</c>. Zero — the default —
    /// means the jurisdiction has no such rule.
    /// </summary>
    /// <remarks>
    /// A <see cref="decimal"/> rather than a <c>Money</c>, because it is a <i>parameter</i> to
    /// rounding and not an amount anything is added to: it is divided by, never summed.
    /// <c>CashRounding.ToIncrement</c> takes it in that form for the same reason.
    /// <para>
    /// The adjustment it produces is recorded on the sale as <c>RoundingAdjustment</c> and
    /// never absorbed into the total — otherwise the drawer is over or short by an amount
    /// nothing explains. See <c>Pos.Core.Monetary.CashRounding</c>.
    /// </para>
    /// </remarks>
    public decimal CashRoundingIncrement { get; set; }

    /// <summary>
    /// How far past midnight this tenant's trading day starts, in local time.
    /// </summary>
    /// <remarks>
    /// With an offset of 04:00, a shift closed at 02:00 on Tuesday belongs to Monday's
    /// trading day — which is what the staff who worked it, and the Z-report, both expect.
    /// Applied at query and presentation time; storage stays UTC.
    /// </remarks>
    public TimeSpan BusinessDayStartOffset { get; set; }

    /// <summary>
    /// The trading address, as it appears on a receipt. Free-form and multi-line.
    /// </summary>
    /// <remarks>
    /// Deliberately one unstructured field rather than street/city/postcode columns. A receipt
    /// prints it verbatim and nothing in this system parses it, so structure would buy nothing
    /// and would have to be right for every country the product is sold in.
    /// </remarks>
    public string? AddressLine { get; set; }

    /// <summary>
    /// The shop's VAT or tax registration number, printed on the receipt.
    /// </summary>
    /// <remarks>
    /// Legally required on a receipt in most jurisdictions, which is why it lives here rather
    /// than inside <see cref="ReceiptHeader"/> as free text: it is a distinct fact a tax
    /// authority looks for, and a renderer needs to be able to label it.
    /// </remarks>
    public string? TaxNumber { get; set; }

    /// <summary>Free text above the sale on a receipt — a strapline, a phone number.</summary>
    public string? ReceiptHeader { get; set; }

    /// <summary>Free text below the totals — "returns within 30 days with this receipt".</summary>
    public string? ReceiptFooter { get; set; }

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
