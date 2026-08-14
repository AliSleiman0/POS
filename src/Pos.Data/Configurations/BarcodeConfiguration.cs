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
        //
        // FILTERED on deleted_at IS NULL since Phase 9 made removal a soft delete. Without the
        // filter a withdrawn code could never be re-added — mis-scan a label, remove it, scan
        // the right one, and a 23505 comes back for a code the shop cannot see anywhere. The
        // uniqueness that matters is among the codes that still scan.
        builder.HasIndex(b => new { b.TenantId, b.Code })
            .IsUnique()
            .HasFilter("deleted_at IS NULL")
            .HasDatabaseName("ux_barcode_tenant_code");

        // "The barcodes of this product", and the index the RESTRICT above needs when a
        // product deletion is attempted.
        builder.HasIndex(b => new { b.TenantId, b.ProductId })
            .HasDatabaseName("ix_barcode_tenant_product");

        // The catalog sync feed's keyset — "every barcode changed since <watermark>" — is
        // served by an EXPRESSION index on (tenant_id, COALESCE(updated_at, created_at), id),
        // created in the CatalogSync migration rather than here.
        //
        // Not expressible through HasIndex: EF has no expression-index API, and a composite
        // index on the two columns would not serve an ORDER BY over their COALESCE. It has to
        // be COALESCE because UpdatedAt is null until a row is first edited — ordering on
        // updated_at alone would sort every never-edited barcode into one undifferentiated
        // null bucket, and a keyset cannot page through that.
    }
}
