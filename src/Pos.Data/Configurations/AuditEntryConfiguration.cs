using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("audit_entry");
        builder.HasKey(a => a.Id);

        // Text for the same reason the stock ledger's type is: this table is read by a shop
        // owner asking what happened, and `4` answers nothing while `RefundIssued` does.
        builder.HasEnumAsText(a => a.Action, "action");

        builder.HasBoundedText(a => a.EntityType, "entity_type", AuditEntry.EntityTypeMaxLength);

        // jsonb rather than text, which is the opposite of the call IdempotencyRecord makes
        // for its stored response — and for the opposite reason. That column has to replay
        // byte-identically, so Postgres reordering keys would break it. These are read by a
        // person and, later, filtered on; normalising them is the behaviour we want.
        builder.Property(a => a.Before).HasColumnType("jsonb");
        builder.Property(a => a.After).HasColumnType("jsonb");

        // Composite, like every other tenant-scoped relationship: referential-integrity checks
        // are exempt from row-level security, so a bare register_id would accept another
        // tenant's till. Uses the same principal key ShiftConfiguration already asks for, so
        // this adds no schema to `register`.
        builder.HasOne<Register>()
            .WithMany()
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .HasForeignKey(a => new { a.TenantId, a.RegisterId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_audit_entry_register");

        // The one index docs/DATA-MODEL.md specifies, and the one the read screen wants:
        // "this shop's log, newest first" is the whole default view, and the keyset pages on
        // it. ?action= and ?actorId= filter on top of that scan — a shop's audit table is
        // small, and an index per filter would be three indexes serving one screen.
        builder.HasIndex(a => new { a.TenantId, a.OccurredAt })
            .HasDatabaseName("ix_audit_entry_tenant_occurred_at");

        // No foreign key on ActorId, matching Sale.CashierId, SaleLine.OverriddenBy,
        // StockMovement.PerformedBy and Shift.OpenedBy. One would need an alternate key on
        // Identity's user table that nothing else asks for. Note the consequence, since this
        // is the trap StockMovementConfiguration documents for SaleId: TenantModelTests only
        // inspects foreign keys that exist, so nothing here fails the build — the column is
        // deliberately unconstrained, not accidentally so.
    }
}
