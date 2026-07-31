using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("refresh_token");
        builder.HasKey(t => t.Id);

        builder.HasBoundedText(t => t.TokenHash, "token_hash", RefreshToken.TokenHashLength);

        // Leading with tenant_id, per docs/DATA-MODEL.md: every query already filters by
        // tenant, so a tenant-first index is the one the planner can actually use.
        builder.HasIndex(t => new { t.TenantId, t.TokenHash })
            .IsUnique()
            .HasDatabaseName("ux_refresh_token_tenant_hash");

        // Revoking a family is a single ranged delete rather than a scan.
        builder.HasIndex(t => new { t.TenantId, t.FamilyId })
            .HasDatabaseName("ix_refresh_token_tenant_family");

        builder.HasIndex(t => new { t.TenantId, t.UserId })
            .HasDatabaseName("ix_refresh_token_tenant_user");
    }
}
