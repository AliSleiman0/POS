using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class DiningTableConfiguration : IEntityTypeConfiguration<DiningTable>
{
    public void Configure(EntityTypeBuilder<DiningTable> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // `table` is a reserved word in SQL. See the remarks on DiningTable.
        builder.ToTable("dining_table");
        builder.HasKey(t => t.Id);

        // Pointed at by customer_order.
        builder.HasAlternateKey(t => new { t.TenantId, t.Id })
            .HasName("ak_dining_table_tenant_id_id");

        builder.HasBoundedText(t => t.Name, "name", DiningTable.NameMaxLength);

        builder.Property(t => t.IsActive).HasDefaultValue(true);

        builder.HasOne<ServiceArea>()
            .WithMany()
            .HasPrincipalKey(a => new { a.TenantId, a.Id })
            .HasForeignKey(t => new { t.TenantId, t.ServiceAreaId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_dining_table_service_area");

        // Seats is advisory, but a negative one is a typo that would make a cover count
        // nonsensical rather than merely optimistic.
        builder.ToTable(t => t.HasCheckConstraint(
            "ck_dining_table_seats_range",
            "seats >= 0"));

        // "Table 4" has to mean one table. Two of them is a floor plan nobody can work, and a
        // transfer that names one by its label would pick arbitrarily between them.
        builder.HasIndex(t => new { t.TenantId, t.Name })
            .IsUnique()
            .HasDatabaseName("ux_dining_table_tenant_name");

        // The floor view: every table in an area, in display order.
        builder.HasIndex(t => new { t.TenantId, t.ServiceAreaId, t.SortOrder })
            .HasDatabaseName("ix_dining_table_tenant_area_sort");
    }
}
