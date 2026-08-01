using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class SaleLineConfiguration : IEntityTypeConfiguration<SaleLine>
{
    public void Configure(EntityTypeBuilder<SaleLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sale_line");
        builder.HasKey(l => l.Id);

        // A refund line points at the original line it reverses, so sale_line is a principal
        // key as well as a dependent.
        builder.HasAlternateKey(l => new { l.TenantId, l.Id }).HasName("ak_sale_line_tenant_id_id");

        builder.HasBoundedText(l => l.Description, "description", SaleLine.DescriptionMaxLength);

        builder.HasOne<Sale>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(l => new { l.TenantId, l.SaleId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sale_line_sale");

        // Restrict, never Cascade — a product is withdrawn with IsActive and never deleted,
        // and a cascade here would take a customer's sales history with it.
        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(l => new { l.TenantId, l.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sale_line_product");

        builder.HasOne<SaleLine>()
            .WithMany()
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .HasForeignKey(l => new { l.TenantId, l.OriginalSaleLineId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sale_line_original");

        // Line numbers are what the receipt prints and what a refund request names, so two
        // lines sharing one on the same sale is a genuine ambiguity rather than untidiness.
        builder.HasIndex(l => new { l.TenantId, l.SaleId, l.LineNumber })
            .IsUnique()
            .HasDatabaseName("ux_sale_line_tenant_sale_line_number");

        // The sale-detail load: "give me this sale's lines".
        builder.HasIndex(l => new { l.TenantId, l.SaleId })
            .HasDatabaseName("ix_sale_line_tenant_sale");

        // "How much of this line has already been refunded?" — summed on every refund, and
        // the reason OriginalSaleLineId exists at all.
        builder.HasIndex(l => new { l.TenantId, l.OriginalSaleLineId })
            .HasDatabaseName("ix_sale_line_tenant_original");
    }
}
