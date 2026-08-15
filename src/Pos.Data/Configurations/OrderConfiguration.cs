using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // `order` is a reserved word in SQL. See the remarks on Order.
        builder.ToTable("customer_order");
        builder.HasKey(o => o.Id);

        // Pointed at by customer_order_line and by order_bill.
        builder.HasAlternateKey(o => new { o.TenantId, o.Id })
            .HasName("ak_customer_order_tenant_id_id");

        builder.HasEnumAsText(o => o.Type, "type");
        builder.HasEnumAsText(o => o.Status, "status");

        builder.HasBoundedText(o => o.TabName, "tab_name", Order.TabNameMaxLength);
        builder.HasBoundedText(o => o.Note, "note", Order.NoteMaxLength);
        builder.HasBoundedText(o => o.AbandonReason, "abandon_reason", Order.AbandonReasonMaxLength);

        builder.HasOne<DiningTable>()
            .WithMany()
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .HasForeignKey(o => new { o.TenantId, o.DiningTableId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_order_dining_table");

        builder.HasOne<Register>()
            .WithMany()
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .HasForeignKey(o => new { o.TenantId, o.RegisterId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_customer_order_register");

        // The human reference, unique per tenant. Its own series, not the sale's — see
        // OrderSequence for why sharing one would put unexplained gaps in the sale numbers.
        builder.HasIndex(o => new { o.TenantId, o.OrderNumber })
            .IsUnique()
            .HasDatabaseName("ux_customer_order_tenant_order_number");

        /*
         * At most one open order per table.
         *
         * A filtered unique index rather than a check-then-insert, exactly as
         * ux_shift_tenant_register_open is and for the same reason: two staff seating the same
         * table at the same moment both pass "is anything open here?" and both insert. That
         * leaves a table with two bills, and the next round of drinks goes onto whichever one
         * the query happened to return — which nobody notices until one of them is paid and the
         * other is not.
         *
         * Filtered on the table id being present as well as the status, because tabs and
         * takeaways have no table and several of them are open at once quite properly. A NULL
         * would not collide in a unique index anyway; the predicate says so out loud.
         */
        builder.HasIndex(o => new { o.TenantId, o.DiningTableId })
            .IsUnique()
            .HasFilter("status = 'Open' AND dining_table_id IS NOT NULL")
            .HasDatabaseName("ux_customer_order_tenant_table_open");

        // The floor view's read: everything still open, oldest first. Covers the far more
        // common query than "every order ever", which only reporting asks.
        builder.HasIndex(o => new { o.TenantId, o.Status, o.OpenedAt })
            .HasDatabaseName("ix_customer_order_tenant_status_opened_at");
    }
}
