using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class SaleConfiguration : IEntityTypeConfiguration<Sale>
{
    public void Configure(EntityTypeBuilder<Sale> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sale");
        builder.HasKey(s => s.Id);

        // Pointed at by sale_line, tender, stock_movement, stock_discrepancy and by refunds
        // linking back to their original.
        builder.HasAlternateKey(s => new { s.TenantId, s.Id }).HasName("ak_sale_tenant_id_id");

        builder.HasEnumAsText(s => s.Type, "type");
        builder.HasEnumAsText(s => s.Status, "status");
        builder.HasEnumAsText(s => s.TaxMode, "tax_mode");

        builder.HasBoundedText(s => s.VoidReason, "void_reason", Sale.VoidReasonMaxLength);
        builder.HasBoundedText(s => s.RefundReason, "refund_reason", Sale.RefundReasonMaxLength);

        builder.HasOne<Shift>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(s => new { s.TenantId, s.ShiftId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sale_shift");

        builder.HasOne<Register>()
            .WithMany()
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .HasForeignKey(s => new { s.TenantId, s.RegisterId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sale_register");

        // A refund points at the sale it reverses. Self-referencing and tenant-scoped, the
        // same shape as category's parent.
        builder.HasOne<Sale>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(s => new { s.TenantId, s.OriginalSaleId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_sale_original");

        // THE idempotency guarantee, and it is the index rather than a pre-check: two
        // concurrent submissions of one cart both pass "does this exist yet?" and both
        // insert. The loser blocks here until the winner commits, then gets a 23505 with the
        // winner's row already visible to re-read.
        builder.HasIndex(s => new { s.TenantId, s.ClientTransactionId })
            .IsUnique()
            .HasDatabaseName("ux_sale_tenant_client_transaction_id");

        // The human reference. Unique per tenant, and the counter row is what keeps it
        // gapless — this index is what proves the counter worked.
        builder.HasIndex(s => new { s.TenantId, s.SaleNumber })
            .IsUnique()
            .HasDatabaseName("ux_sale_tenant_sale_number");

        // Reporting date ranges, and the keyset GET /sales pages on.
        builder.HasIndex(s => new { s.TenantId, s.CompletedAt })
            .HasDatabaseName("ix_sale_tenant_completed_at");

        // "This shift's sales" — the read the close arithmetic and the Z-report both make.
        builder.HasIndex(s => new { s.TenantId, s.ShiftId })
            .HasDatabaseName("ix_sale_tenant_shift");

        // "Has this sale already been refunded, and by how much?" — asked on every refund and
        // every void, so it is not an optional convenience.
        builder.HasIndex(s => new { s.TenantId, s.OriginalSaleId })
            .HasDatabaseName("ix_sale_tenant_original");
    }
}
