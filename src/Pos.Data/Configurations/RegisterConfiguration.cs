using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class RegisterConfiguration : IEntityTypeConfiguration<Register>
{
    public void Configure(EntityTypeBuilder<Register> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("register");
        builder.HasKey(r => r.Id);

        // Declared rather than left implicit, which is what it was until Phase 7. Shift's
        // HasPrincipalKey had been creating this key as a side effect, and EF derived its
        // name from whichever entity happened to ask first — so adding a second dependent
        // (audit_entry) generated a migration that renamed it for no reason. Naming it here
        // pins it, and matches every other tenant-referenced entity.
        builder.HasAlternateKey(r => new { r.TenantId, r.Id }).HasName("ak_register_tenant_id_id");

        builder
            .HasBoundedText(r => r.Name, "name", Register.NameMaxLength)
            .HasBoundedText(r => r.DeviceTokenHash, "device_token_hash", Register.DeviceTokenHashLength);

        // The lookup on every device-authenticated request. Unique so one token cannot
        // somehow address two tills, and filtered so revoked registers (hash null) do not
        // occupy index space.
        builder.HasIndex(r => new { r.TenantId, r.DeviceTokenHash })
            .IsUnique()
            .HasFilter("device_token_hash IS NOT NULL")
            .HasDatabaseName("ux_register_tenant_device_token_hash");

        builder.Property(r => r.IsActive).HasDefaultValue(true);
    }
}
