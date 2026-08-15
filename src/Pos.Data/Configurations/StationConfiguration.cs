using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class StationConfiguration : IEntityTypeConfiguration<Station>
{
    public void Configure(EntityTypeBuilder<Station> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("station");
        builder.HasKey(s => s.Id);

        // Pointed at by kitchen_ticket, product and category.
        builder.HasAlternateKey(s => new { s.TenantId, s.Id })
            .HasName("ak_station_tenant_id_id");

        builder.HasBoundedText(s => s.Name, "name", Station.NameMaxLength);

        builder.Property(s => s.IsActive).HasDefaultValue(true);

        // "Grill" has to mean one station. Two of them is a kitchen where half the tickets go
        // to a screen nobody is standing at, and the routing on a category would pick between
        // them by whichever id a manager happened to click.
        builder.HasIndex(s => new { s.TenantId, s.Name })
            .IsUnique()
            .HasDatabaseName("ux_station_tenant_name");

        builder.HasIndex(s => new { s.TenantId, s.SortOrder, s.Name })
            .HasDatabaseName("ix_station_tenant_sort");
    }
}
