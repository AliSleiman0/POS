using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class CategoryConfiguration : IEntityTypeConfiguration<Category>
{
    public void Configure(EntityTypeBuilder<Category> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("category");
        builder.HasKey(c => c.Id);

        builder.HasAlternateKey(c => new { c.TenantId, c.Id }).HasName("ak_category_tenant_id_id");

        builder.HasBoundedText(c => c.Name, "name", Category.NameMaxLength);

        // Self-referencing and tenant-scoped: a category's parent must be a category at the
        // same shop. Optional because ParentCategoryId is nullable — Postgres does not check
        // a foreign key whose columns include a NULL, so a top-level category passes.
        builder.HasOne<Category>()
            .WithMany()
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .HasForeignKey(c => new { c.TenantId, c.ParentCategoryId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_category_parent");

        // Where a restaurant actually configures kitchen routing. Optional for the same reason
        // the parent above is: Postgres does not check a foreign key whose columns include a
        // NULL, so a category that routes nowhere passes.
        builder.HasOne<Station>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(c => new { c.TenantId, c.StationId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_category_station");

        builder.HasIndex(c => new { c.TenantId, c.ParentCategoryId })
            .HasDatabaseName("ix_category_tenant_parent");

        // Without it, retiring a station scans every category to evaluate the RESTRICT.
        builder.HasIndex(c => new { c.TenantId, c.StationId })
            .HasDatabaseName("ix_category_tenant_station");

        // GET /categories orders by (name, id) and pages by keyset. SortOrder is what the
        // client arranges its picker by, but it is neither unique nor indexed, so it cannot
        // be the sort key a cursor resumes from — a keyset needs a total order.
        builder.HasIndex(c => new { c.TenantId, c.Name })
            .HasDatabaseName("ix_category_tenant_name");

        builder.Property(c => c.IsActive).HasDefaultValue(true);
    }
}
