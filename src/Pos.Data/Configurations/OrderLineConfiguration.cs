using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class OrderLineConfiguration : IEntityTypeConfiguration<OrderLine>
{
    public void Configure(EntityTypeBuilder<OrderLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customer_order_line");
        builder.HasKey(l => l.Id);

        // Pointed at by its own modifier children, by kitchen_ticket_line and by order_bill_line.
        builder.HasAlternateKey(l => new { l.TenantId, l.Id })
            .HasName("ak_customer_order_line_tenant_id_id");

        builder.HasEnumAsText(l => l.Status, "status");

        builder.HasBoundedText(l => l.Description, "description", OrderLine.DescriptionMaxLength);
        builder.HasBoundedText(l => l.Note, "note", OrderLine.NoteMaxLength);
        builder.HasBoundedText(l => l.VoidReason, "void_reason", OrderLine.VoidReasonMaxLength);

        builder.HasOne<Order>()
            .WithMany()
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .HasForeignKey(l => new { l.TenantId, l.OrderId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_order_line_order");

        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(l => new { l.TenantId, l.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_order_line_product");

        // A modifier points at the line it modifies. Self-referencing and tenant-scoped, the
        // same shape as a refund pointing at its original sale.
        builder.HasOne<OrderLine>()
            .WithMany()
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .HasForeignKey(l => new { l.TenantId, l.ParentOrderLineId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_order_line_parent");

        // Ordering food is not returning it: a negative or zero quantity here would price a
        // line the customer is owed money for, on an order that has no refund path. A return
        // is a refund against the settled sale, which Phase 3.7 already owns.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_customer_order_line_quantity_positive",
            "quantity > 0"));

        // Courses are 1-based and a seat, when there is one, is a real seat. Both are keyed by
        // a person under pressure and both are read straight back out onto a kitchen ticket.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_customer_order_line_course_positive",
            "course >= 1"));

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_customer_order_line_seat_positive",
            "seat_number IS NULL OR seat_number >= 1"));

        // The order screen's read, and the one every price, fire and bill starts from.
        builder.HasIndex(l => new { l.TenantId, l.OrderId, l.LineNumber })
            .HasDatabaseName("ix_customer_order_line_tenant_order_line_number");

        // "What hangs off this line?" — asked whenever a parent is voided, re-quantified or
        // allocated to a bill, because its modifiers have to travel with it.
        builder.HasIndex(l => new { l.TenantId, l.ParentOrderLineId })
            .HasDatabaseName("ix_customer_order_line_tenant_parent");
    }
}
