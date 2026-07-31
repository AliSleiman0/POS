using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product");
        builder.HasKey(p => p.Id);

        builder.HasAlternateKey(p => new { p.TenantId, p.Id }).HasName("ak_product_tenant_id_id");

        builder
            .HasBoundedText(p => p.Sku, "sku", Product.SkuMaxLength)
            .HasBoundedText(p => p.Name, "name", Product.NameMaxLength)
            .HasBoundedText(p => p.Description, "description", Product.DescriptionMaxLength)
            .HasEnumAsText(p => p.Unit, "unit");

        // Optional — an uncategorised product is sellable, just harder to find.
        builder.HasOne<Category>()
            .WithMany()
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .HasForeignKey(p => new { p.TenantId, p.CategoryId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_category");

        // Required: a product with no tax class cannot be priced.
        builder.HasOne<TaxClass>()
            .WithMany()
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .HasForeignKey(p => new { p.TenantId, p.TaxClassId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_tax_class");

        // Business identity. Unique on the stored value, which is only as case-insensitive
        // as what gets written into it — Product.NormalizeSku is the other half.
        builder.HasIndex(p => new { p.TenantId, p.Sku })
            .IsUnique()
            .HasDatabaseName("ux_product_tenant_sku");

        // Catalog listing and prefix search. NOTE for 2.2: this serves ordering and
        // "starts with" only. The case-insensitive contains search that milestone promises
        // will sequential-scan against it — that needs lower(name) or pg_trgm, decided
        // there rather than guessed here.
        builder.HasIndex(p => new { p.TenantId, p.Name })
            .HasDatabaseName("ix_product_tenant_name");

        // Both foreign keys get their own index: without one, deleting a category or a tax
        // class scans every product to evaluate the RESTRICT.
        builder.HasIndex(p => new { p.TenantId, p.CategoryId })
            .HasDatabaseName("ix_product_tenant_category");

        builder.HasIndex(p => new { p.TenantId, p.TaxClassId })
            .HasDatabaseName("ix_product_tenant_tax_class");

        builder.Property(p => p.IsActive).HasDefaultValue(true);

        // The sentinel is stated rather than inferred, and it is load-bearing. EF decides
        // whether to send a property on INSERT by comparing it to the sentinel — the value
        // meaning "not set". With a store default of true and no sentinel, an explicit
        // TrackStock = false can be read as "unset", omitted from the INSERT, and turned
        // into true by the database: a service item that silently starts tracking stock.
        // Round-tripped by a test, because nothing else would notice.
        builder.Property(p => p.TrackStock).HasDefaultValue(true).HasSentinel(true);
    }
}
