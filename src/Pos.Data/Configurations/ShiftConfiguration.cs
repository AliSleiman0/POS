using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class ShiftConfiguration : IEntityTypeConfiguration<Shift>
{
    public void Configure(EntityTypeBuilder<Shift> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("shift");
        builder.HasKey(s => s.Id);

        // Sale and CashMovement both point at a shift, and their foreign keys carry the
        // tenant, so this is the principal key they need.
        builder.HasAlternateKey(s => new { s.TenantId, s.Id }).HasName("ak_shift_tenant_id_id");

        builder.HasEnumAsText(s => s.Status, "status");

        builder.HasOne<Register>()
            .WithMany()
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .HasForeignKey(s => new { s.TenantId, s.RegisterId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_shift_register");

        // At most one open shift per register, and a filtered unique index rather than a
        // check-then-insert: two tills opening at the same moment both pass a pre-check and
        // both insert, leaving a register with two open drawers that cannot be reconciled.
        // The index makes the loser a caught 23505. Same shape as ux_tax_class_tenant_default.
        builder.HasIndex(s => new { s.TenantId, s.RegisterId })
            .IsUnique()
            .HasFilter("status = 'Open'")
            .HasDatabaseName("ux_shift_tenant_register_open");

        // "This register's shifts, most recent first" is the read a Z-report screen wants,
        // and the keyset pages on the open time.
        builder.HasIndex(s => new { s.TenantId, s.OpenedAt })
            .HasDatabaseName("ix_shift_tenant_opened_at");
    }
}
