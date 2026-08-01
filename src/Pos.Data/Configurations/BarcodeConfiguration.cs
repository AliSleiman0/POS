using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class BarcodeConfiguration : IEntityTypeConfiguration<Barcode>
{
    public void Configure(EntityTypeBuilder<Barcode> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("barcode");
        builder.HasKey(b => b.Id);

        builder.HasBoundedText(b => b.Code, "code", Barcode.CodeMaxLength);

        // The tenant travels inside the foreign key, and this is the reason:
        // **Postgres exempts referential-integrity checks from row-level security.** A
        // single-column key on product_id would let tenant B insert a barcode pointing at
        // tenant A's product, and the RI check would accept it — the policy that hides the
        // row does not apply to the check. Carrying tenant_id makes that a 23503 violation
        // instead of a silent cross-tenant reference. Do not "simplify" it back.
        //
        // HasPrincipalKey comes first deliberately: called the other way round, EF matches
        // the two foreign-key properties against the principal's single-column primary key
        // and throws about the number of properties not matching.
        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(b => new { b.TenantId, b.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_barcode_product");

        // The hottest read in the system: every scan at every till.
        builder.HasIndex(b => new { b.TenantId, b.Code })
            .IsUnique()
            .HasDatabaseName("ux_barcode_tenant_code");

        // "The barcodes of this product", and the index the RESTRICT above needs when a
        // product deletion is attempted.
        builder.HasIndex(b => new { b.TenantId, b.ProductId })
            .HasDatabaseName("ix_barcode_tenant_product");
    }
}
