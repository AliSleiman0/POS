using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class StockDiscrepancyConfiguration : IEntityTypeConfiguration<StockDiscrepancy>
{
    public void Configure(EntityTypeBuilder<StockDiscrepancy> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_discrepancy");
        builder.HasKey(d => d.Id);

        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(d => new { d.TenantId, d.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_discrepancy_product");

        builder.HasOne<Sale>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(d => new { d.TenantId, d.SaleId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_discrepancy_sale");

        builder.HasOne<SaleLine>()
            .WithMany()
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .HasForeignKey(d => new { d.TenantId, d.SaleLineId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_discrepancy_sale_line");

        // Oldest first, like the stock ledger: the list reads as a history of what went wrong
        // and in what order, and this is the keyset it pages on.
        builder.HasIndex(d => new { d.TenantId, d.DetectedAt })
            .HasDatabaseName("ix_stock_discrepancy_tenant_detected");
    }
}
