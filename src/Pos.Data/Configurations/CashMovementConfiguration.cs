using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class CashMovementConfiguration : IEntityTypeConfiguration<CashMovement>
{
    public void Configure(EntityTypeBuilder<CashMovement> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("cash_movement");
        builder.HasKey(m => m.Id);

        builder.HasEnumAsText(m => m.Type, "type");

        builder.HasBoundedText(m => m.Reason, "reason", CashMovement.ReasonMaxLength);

        builder.HasOne<Shift>()
            .WithMany()
            .HasPrincipalKey(s => new { s.TenantId, s.Id })
            .HasForeignKey(m => new { m.TenantId, m.ShiftId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_cash_movement_shift");

        // "This shift's cash movements, in the order they happened" — what the close sums and
        // what a Z-report prints.
        builder.HasIndex(m => new { m.TenantId, m.ShiftId, m.OccurredAt })
            .HasDatabaseName("ix_cash_movement_tenant_shift_occurred");
    }
}
