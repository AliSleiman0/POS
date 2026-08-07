using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tenant");
        builder.HasKey(t => t.Id);

        builder
            .HasBoundedText(t => t.Name, "name", 200)
            .HasBoundedText(t => t.Slug, "slug", Tenant.SlugMaxLength)
            .HasBoundedText(t => t.CurrencyCode, "currency_code", 3)
            .HasBoundedText(t => t.TimeZoneId, "time_zone_id", 64)
            .HasEnumAsText(t => t.TaxMode, "tax_mode")

            // The receipt header block. All nullable: a shop that has not filled them in
            // still prints a receipt, with the lines it has no content for simply absent.
            .HasBoundedText(t => t.AddressLine, "address_line", Tenant.AddressLineMaxLength)
            .HasBoundedText(t => t.TaxNumber, "tax_number", Tenant.TaxNumberMaxLength)
            .HasBoundedText(t => t.ReceiptHeader, "receipt_header", Tenant.ReceiptTextMaxLength)
            .HasBoundedText(t => t.ReceiptFooter, "receipt_footer", Tenant.ReceiptTextMaxLength);

        // Unique across the platform, not per tenant — it is the pre-authentication
        // handle a client presents at login, so two tenants sharing one is not a
        // conflict to resolve later, it is an ambiguous login.
        builder.HasIndex(t => t.Slug).IsUnique().HasDatabaseName("ux_tenant_slug");

        // Slugs are matched by exact equality against normalised input, so the shape is
        // enforced here rather than trusted from whatever created the row.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_tenant_slug_format",
            "slug ~ '^[a-z0-9]+(-[a-z0-9]+)*$'"));

        // Zero means "no cash rounding", which is the default and the common case. Negative
        // would invert the rounding direction and silently move every cash total the wrong
        // way, so the column refuses it rather than trusting whatever wrote the row.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_tenant_cash_rounding_increment_range",
            "cash_rounding_increment >= 0"));

        builder.Property(t => t.BusinessDayStartOffset).HasColumnType("interval");

        builder.Property(t => t.IsActive).HasDefaultValue(true);
    }
}
