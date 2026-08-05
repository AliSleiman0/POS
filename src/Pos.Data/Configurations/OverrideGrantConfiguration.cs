using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class OverrideGrantConfiguration : IEntityTypeConfiguration<OverrideGrant>
{
    public void Configure(EntityTypeBuilder<OverrideGrant> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("override_grant");
        builder.HasKey(g => g.Id);

        builder.HasBoundedText(g => g.TokenHash, "token_hash", OverrideGrant.TokenHashLength);

        // text[] rather than a joined table or a comma-joined string. Npgsql maps it natively,
        // the set is two entries at most, and it is read whole every time it is read at all.
        builder.Property(g => g.Policies)
            .HasColumnName("policies")
            .IsRequired();

        // Tenant-first, per docs/DATA-MODEL.md: every query already filters by tenant, so a
        // tenant-first index is the one the planner can use. Unique because presenting a grant
        // is a lookup by hash, and two rows sharing one would make "which grant is this?"
        // ambiguous at the moment it is being spent.
        builder.HasIndex(g => new { g.TenantId, g.TokenHash })
            .IsUnique()
            .HasDatabaseName("ux_override_grant_tenant_hash");

        // For sweeping expired rows, and for answering "what did this manager authorise?"
        // once Phase 7.2 has somewhere to show it.
        builder.HasIndex(g => new { g.TenantId, g.UserId })
            .HasDatabaseName("ix_override_grant_tenant_user");

        // Tenant-first like the rest, and enforced: TenantModelTests fails the build on an index
        // that leads with anything else, because every query is already filtered by tenant and
        // an expiry-first index is one the planner will not reach for.
        builder.HasIndex(g => new { g.TenantId, g.ExpiresAt })
            .HasDatabaseName("ix_override_grant_tenant_expires_at");
    }
}
