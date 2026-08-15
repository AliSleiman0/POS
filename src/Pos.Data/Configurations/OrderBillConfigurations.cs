using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class OrderBillConfiguration : IEntityTypeConfiguration<OrderBill>
{
    public void Configure(EntityTypeBuilder<OrderBill> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_bill");
        builder.HasKey(b => b.Id);

        // Pointed at by order_bill_line.
        builder.HasAlternateKey(b => new { b.TenantId, b.Id })
            .HasName("ak_order_bill_tenant_id_id");

        builder.HasEnumAsText(b => b.Status, "status");

        builder.HasOne<Order>()
            .WithMany()
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .HasForeignKey(b => new { b.TenantId, b.OrderId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_bill_order");

        // The link to the money, and it points this way only: Sale gains no OrderId, so the
        // retail path stays unaware any of this exists.
        builder.HasOne<Sale>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(b => new { b.TenantId, b.SaleId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_bill_sale");

        // Bill numbers are per order — "bill two" means one thing at a table.
        builder.HasIndex(b => new { b.TenantId, b.OrderId, b.BillNumber })
            .IsUnique()
            .HasDatabaseName("ux_order_bill_tenant_order_number");

        /*
         * The same guarantee ux_sale_tenant_client_transaction_id gives, one level up.
         *
         * A bill's key is minted when the bill is created and reused on every payment attempt,
         * so two tills settling the same bill at once both reach for one key — and the loser
         * blocks here rather than writing a second sale. The sale table's own unique index is
         * the authority for the sale; this one stops two bills ever sharing an identity.
         */
        builder.HasIndex(b => new { b.TenantId, b.ClientTransactionId })
            .IsUnique()
            .HasDatabaseName("ux_order_bill_tenant_client_transaction_id");
    }
}

internal sealed class OrderBillLineConfiguration : IEntityTypeConfiguration<OrderBillLine>
{
    public void Configure(EntityTypeBuilder<OrderBillLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_bill_line");
        builder.HasKey(l => l.Id);

        builder.HasOne<OrderBill>()
            .WithMany()
            .HasPrincipalKey(b => new { b.TenantId, b.Id })
            .HasForeignKey(l => new { l.TenantId, l.OrderBillId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_bill_line_bill");

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .HasForeignKey(l => new { l.TenantId, l.OrderLineId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_order_bill_line_order_line");

        // Allocating nothing is not a split, it is a row that makes the sums harder to read.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_order_bill_line_quantity_positive",
            "quantity > 0"));

        // One row per line per bill: "half the wine" is a quantity, not two allocations that
        // have to be summed before anybody can tell what this bill takes.
        builder.HasIndex(l => new { l.TenantId, l.OrderBillId, l.OrderLineId })
            .IsUnique()
            .HasDatabaseName("ux_order_bill_line_tenant_bill_line");

        // "How much of this line is already allocated?" — asked on every allocation and on
        // every attempt to close the order.
        builder.HasIndex(l => new { l.TenantId, l.OrderLineId })
            .HasDatabaseName("ix_order_bill_line_tenant_order_line");
    }
}
