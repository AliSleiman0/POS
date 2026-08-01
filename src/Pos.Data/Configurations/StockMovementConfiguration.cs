using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_movement");
        builder.HasKey(m => m.Id);

        // Text, not an int: a ledger is read by people six months later, and `2` in a
        // shrinkage report means nothing while `Waste` means what it says. The check
        // constraint is generated from the enum's names.
        builder.HasEnumAsText(m => m.Type, "type");

        builder.HasBoundedText(m => m.Reason, "reason", StockMovement.ReasonMaxLength);

        // Same reasoning as BarcodeConfiguration, and it is not optional here either:
        // referential-integrity checks are exempt from row-level security, so a
        // single-column product_id would let one tenant write movements against another
        // tenant's product and have the RI check accept them.
        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(m => new { m.TenantId, m.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_movement_product");

        // "This product's ledger, oldest first" — the paged endpoint and the rebuild both
        // walk it in exactly this order, so the index serves the filter and the sort at once.
        builder.HasIndex(m => new { m.TenantId, m.ProductId, m.OccurredAt })
            .HasDatabaseName("ix_stock_movement_tenant_product_occurred");
    }
}
