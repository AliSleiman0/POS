using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class ModifierGroupConfiguration : IEntityTypeConfiguration<ModifierGroup>
{
    public void Configure(EntityTypeBuilder<ModifierGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("modifier_group");
        builder.HasKey(g => g.Id);

        // Pointed at by modifier_option and product_modifier_group.
        builder.HasAlternateKey(g => new { g.TenantId, g.Id })
            .HasName("ak_modifier_group_tenant_id_id");

        builder.HasBoundedText(g => g.Name, "name", ModifierGroup.NameMaxLength);

        builder.Property(g => g.IsActive).HasDefaultValue(true);

        // A minimum below zero is meaningless and a maximum below the minimum makes a group
        // nothing can satisfy — which reads at the till as an item that cannot be ordered, with
        // no message that says why.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_modifier_group_selection_range",
            "min_selections >= 0 AND (max_selections IS NULL OR max_selections >= min_selections)"));

        builder.HasIndex(g => new { g.TenantId, g.SortOrder, g.Name })
            .HasDatabaseName("ix_modifier_group_tenant_sort");
    }
}

internal sealed class ModifierOptionConfiguration : IEntityTypeConfiguration<ModifierOption>
{
    public void Configure(EntityTypeBuilder<ModifierOption> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("modifier_option");
        builder.HasKey(o => o.Id);

        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .HasForeignKey(o => new { o.TenantId, o.ModifierGroupId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_modifier_option_group");

        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(o => new { o.TenantId, o.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_modifier_option_product");

        // One product appears once in a group. Twice would show the same answer twice on the
        // sheet and make "at most one" ambiguous about which of the two it meant.
        builder.HasIndex(o => new { o.TenantId, o.ModifierGroupId, o.ProductId })
            .IsUnique()
            .HasDatabaseName("ux_modifier_option_tenant_group_product");
    }
}

internal sealed class ProductModifierGroupConfiguration : IEntityTypeConfiguration<ProductModifierGroup>
{
    public void Configure(EntityTypeBuilder<ProductModifierGroup> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("product_modifier_group");
        builder.HasKey(p => p.Id);

        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(p => new { p.TenantId, p.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_modifier_group_product");

        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .HasForeignKey(p => new { p.TenantId, p.ModifierGroupId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_product_modifier_group_group");

        // A product asks each question once.
        builder.HasIndex(p => new { p.TenantId, p.ProductId, p.ModifierGroupId })
            .IsUnique()
            .HasDatabaseName("ux_product_modifier_group_tenant_product_group");

        // "What does this item ask?" — the read every order screen makes on every tap.
        builder.HasIndex(p => new { p.TenantId, p.ProductId, p.SortOrder })
            .HasDatabaseName("ix_product_modifier_group_tenant_product_sort");
    }
}
