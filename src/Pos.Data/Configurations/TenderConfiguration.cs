using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class TenderConfiguration : IEntityTypeConfiguration<Tender>
{
    public void Configure(EntityTypeBuilder<Tender> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("tender");
        builder.HasKey(t => t.Id);

        builder.HasEnumAsText(t => t.Method, "method");

        builder.HasBoundedText(t => t.Reference, "reference", Tender.ReferenceMaxLength);

        builder.HasOne<Sale>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(t => new { t.TenantId, t.SaleId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_tender_sale");

        // "This sale's tenders", and the read the shift's cash arithmetic sums over.
        builder.HasIndex(t => new { t.TenantId, t.SaleId })
            .HasDatabaseName("ix_tender_tenant_sale");
    }
}
