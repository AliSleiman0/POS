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

        // Ordering. GET /products sorts by (name, id) and pages by keyset, so this is what
        // the cursor seeks into — a btree, because a GIN index cannot serve ORDER BY.
        builder.HasIndex(p => new { p.TenantId, p.Name })
            .HasDatabaseName("ix_product_tenant_name");

        // Search. Resolved in 2.2: ?q= is a case-insensitive *contains* match, and
        // ILIKE '%q%' cannot use the btree above at any width — it would sequential-scan
        // the catalog on every keystroke of a type-ahead.
        //
        // tenant_id leads deliberately. Every query carries `tenant_id = @p` from the query
        // filter, and DatabaseSchemaTests fails any index on a tenant table that does not
        // lead with it; a GIN index on `name` alone would be caught there. btree_gin
        // supplies the GIN operator class for the uuid column so the two can share one
        // index.
        //
        // Trigrams need three non-wildcard characters to be selective, so a one- or
        // two-letter q still scans. That is accepted: the alternative helps nothing else.
        //
        // The named overload, not HasIndex(properties) again: EF keys an index by its
        // property list, so a second unnamed call on (TenantId, Name) would reconfigure the
        // btree above into a GIN one rather than adding anything. Naming it makes it a
        // distinct index in the model.
        builder.HasIndex(p => new { p.TenantId, p.Name }, "ix_product_tenant_name_trgm")
            .HasDatabaseName("ix_product_tenant_name_trgm")
            .HasMethod("gin")
            .HasOperators("", "gin_trgm_ops");

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
