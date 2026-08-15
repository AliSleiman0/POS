using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class KitchenTicketConfiguration : IEntityTypeConfiguration<KitchenTicket>
{
    public void Configure(EntityTypeBuilder<KitchenTicket> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("kitchen_ticket");
        builder.HasKey(t => t.Id);

        // Pointed at by kitchen_ticket_line.
        builder.HasAlternateKey(t => new { t.TenantId, t.Id })
            .HasName("ak_kitchen_ticket_tenant_id_id");

        builder.HasEnumAsText(t => t.Status, "status");

        builder.HasBoundedText(t => t.OrderLabel, "order_label", KitchenTicket.OrderLabelMaxLength);

        builder.HasOne<Order>()
            .WithMany()
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .HasForeignKey(t => new { t.TenantId, t.OrderId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_kitchen_ticket_order");

        builder.HasOne<Station>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(t => new { t.TenantId, t.StationId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_kitchen_ticket_station");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_kitchen_ticket_course_positive",
            "course >= 1"));

        // A bumped ticket says who cleared it and when; an active one says neither. Enforced
        // rather than trusted, because the display's "how long has this been up?" reads the
        // pair and a half-set row would show a cleared ticket counting up for ever.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_kitchen_ticket_bumped_consistent",
            """
            ("status" = 'Bumped' AND bumped_at IS NOT NULL AND bumped_by IS NOT NULL)
            OR ("status" <> 'Bumped' AND bumped_at IS NULL AND bumped_by IS NULL)
            """));

        // The display's poll: one station's queue, oldest first. Every few seconds, from every
        // screen in the kitchen, so it is the one read in this phase that has to be an index
        // seek rather than a scan.
        builder.HasIndex(t => new { t.TenantId, t.StationId, t.Status, t.FiredAt })
            .HasDatabaseName("ix_kitchen_ticket_tenant_station_status_fired");

        // "What has this table got in the kitchen?" — asked by the floor screen, and by the
        // fire endpoint when it needs to know a round has already gone.
        builder.HasIndex(t => new { t.TenantId, t.OrderId, t.Course })
            .HasDatabaseName("ix_kitchen_ticket_tenant_order_course");
    }
}

internal sealed class KitchenTicketLineConfiguration : IEntityTypeConfiguration<KitchenTicketLine>
{
    public void Configure(EntityTypeBuilder<KitchenTicketLine> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("kitchen_ticket_line");
        builder.HasKey(l => l.Id);

        builder.HasAlternateKey(l => new { l.TenantId, l.Id })
            .HasName("ak_kitchen_ticket_line_tenant_id_id");

        builder.HasBoundedText(l => l.Description, "description", KitchenTicketLine.DescriptionMaxLength);
        builder.HasBoundedText(l => l.ModifierText, "modifier_text", KitchenTicketLine.ModifierTextMaxLength);
        builder.HasBoundedText(l => l.Note, "note", KitchenTicketLine.NoteMaxLength);

        builder.HasOne<KitchenTicket>()
            .WithMany()
            .HasPrincipalKey(t => new { t.TenantId, t.Id })
            .HasForeignKey(l => new { l.TenantId, l.KitchenTicketId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_kitchen_ticket_line_ticket");

        builder.HasOne<OrderLine>()
            .WithMany()
            .HasPrincipalKey(l => new { l.TenantId, l.Id })
            .HasForeignKey(l => new { l.TenantId, l.OrderLineId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_kitchen_ticket_line_order_line");

        builder.ToTable(t => t.HasCheckConstraint(
            "ck_kitchen_ticket_line_quantity_positive",
            "quantity > 0"));

        // Rendering one ticket, in the order the items were keyed.
        builder.HasIndex(l => new { l.TenantId, l.KitchenTicketId, l.LineNumber })
            .HasDatabaseName("ix_kitchen_ticket_line_tenant_ticket_line_number");

        // "Which tickets mentioned this line?" — what a void looks up so the screens holding it
        // can strike it through. There may be more than one: a line fired, recalled and fired
        // again appears on each round it was sent in.
        builder.HasIndex(l => new { l.TenantId, l.OrderLineId })
            .HasDatabaseName("ix_kitchen_ticket_line_tenant_order_line");
    }
}
