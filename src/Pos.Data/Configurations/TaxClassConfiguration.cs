using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class TaxClassConfiguration : IEntityTypeConfiguration<TaxClass>
{
    public void Configure(EntityTypeBuilder<TaxClass> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tax_class");
        builder.HasKey(t => t.Id);

        // The principal key every tenant-scoped foreign key points at. See
        // BarcodeConfiguration for why the tenant travels inside the key rather than
        // being checked alongside it.
        builder.HasAlternateKey(t => new { t.TenantId, t.Id }).HasName("ak_tax_class_tenant_id_id");

        builder.HasBoundedText(t => t.Name, "name", TaxClass.NameMaxLength);

        // Overrides the model-wide numeric(19,4) convention. A rate is a fraction, not an
        // amount, and (19,4) would store a mistyped 20 (2,000%) without complaint.
        builder.Property(t => t.Rate).HasPrecision(TaxClass.RatePrecision, TaxClass.RateScale);

        // numeric(6,4) still permits 99.9999. The range is the part a person gets wrong —
        // entering 20 for "20%" is the single most likely tax mistake there is.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_tax_class_rate_range",
            "rate >= 0 AND rate <= 1"));

        // At most one default per tenant. Two would price a new product differently
        // depending on which row was read first, which is the kind of bug that shows up as
        // "sometimes the tax is wrong" months later.
        builder.HasIndex(t => t.TenantId)
            .IsUnique()
            .HasFilter("is_default")
            .HasDatabaseName("ux_tax_class_tenant_default");

        // GET /tax-classes orders by (name, id) and pages by keyset, same as the other two
        // catalog lists. A tenant holds a handful of these and will never reach a second
        // page — the index is here so every list endpoint has one shape and one plan, not
        // because this one is large.
        builder.HasIndex(t => new { t.TenantId, t.Name })
            .HasDatabaseName("ix_tax_class_tenant_name");
    }
}
