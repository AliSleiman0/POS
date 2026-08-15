using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class ServiceAreaConfiguration : IEntityTypeConfiguration<ServiceArea>
{
    public void Configure(EntityTypeBuilder<ServiceArea> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("service_area");
        builder.HasKey(a => a.Id);

        // Pointed at by dining_table.
        builder.HasAlternateKey(a => new { a.TenantId, a.Id })
            .HasName("ak_service_area_tenant_id_id");

        builder.HasBoundedText(a => a.Name, "name", ServiceArea.NameMaxLength);

        builder.Property(a => a.IsActive).HasDefaultValue(true);

        // The floor view's ordering. Not unique — sort_order is a display position and two
        // areas sharing one is a shrug, not a conflict, so the name breaks the tie.
        builder.HasIndex(a => new { a.TenantId, a.SortOrder, a.Name })
            .HasDatabaseName("ix_service_area_tenant_sort");
    }
}
