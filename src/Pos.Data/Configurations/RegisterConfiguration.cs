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
