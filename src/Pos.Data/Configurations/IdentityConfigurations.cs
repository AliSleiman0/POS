using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Tenancy;
using Pos.Data.Identity;

namespace Pos.Data.Configurations;

internal sealed class ApplicationUserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("application_user");

        builder
            .HasBoundedText(u => u.DisplayName, "display_name", 100)
            .HasBoundedText(u => u.Email, "email", 256)
            .HasBoundedText(u => u.NormalizedEmail, "normalized_email", 256)
            .HasBoundedText(u => u.UserName, "user_name", 256)
            .HasBoundedText(u => u.NormalizedUserName, "normalized_user_name", 256);

        // Identity declares these as platform-wide. Left alone, one person could not hold
        // accounts at two tenants — a franchise owner, a consultant, or us doing support —
        // and onboarding would fail with a duplicate-key error that reads like a bug in
        // the onboarding code rather than a deliberate constraint.
        builder.RemoveIndexOn(nameof(ApplicationUser.NormalizedUserName));
        builder.RemoveIndexOn(nameof(ApplicationUser.NormalizedEmail));

        builder.HasIndex(u => new { u.TenantId, u.NormalizedUserName })
            .IsUnique()
            .HasDatabaseName("ux_application_user_tenant_normalized_user_name");

        // Unique within the tenant, and enforced here rather than only by Identity's
        // RequireUniqueEmail option: that option is a query, and a query loses a race that
        // a unique index wins.
        builder.HasIndex(u => new { u.TenantId, u.NormalizedEmail })
            .IsUnique()
            .HasDatabaseName("ux_application_user_tenant_normalized_email");
    }
}

internal sealed class ApplicationRoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("application_role");

        builder
            .HasBoundedText(r => r.Name, "name", 64)
            .HasBoundedText(r => r.NormalizedName, "normalized_name", 64);
    }
}

internal sealed class ApplicationUserRoleConfiguration : IEntityTypeConfiguration<ApplicationUserRole>
{
    public void Configure(EntityTypeBuilder<ApplicationUserRole> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_role");
        builder.HasIndex(ur => new { ur.TenantId, ur.UserId }).HasDatabaseName("ix_user_role_tenant_user");
    }
}

internal sealed class ApplicationUserClaimConfiguration : IEntityTypeConfiguration<ApplicationUserClaim>
{
    public void Configure(EntityTypeBuilder<ApplicationUserClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_claim");
    }
}

internal sealed class ApplicationUserLoginConfiguration : IEntityTypeConfiguration<ApplicationUserLogin>
{
    public void Configure(EntityTypeBuilder<ApplicationUserLogin> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_login");
    }
}

internal sealed class ApplicationUserTokenConfiguration : IEntityTypeConfiguration<ApplicationUserToken>
{
    public void Configure(EntityTypeBuilder<ApplicationUserToken> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("user_token");
    }
}

internal sealed class ApplicationRoleClaimConfiguration : IEntityTypeConfiguration<ApplicationRoleClaim>
{
    public void Configure(EntityTypeBuilder<ApplicationRoleClaim> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("role_claim");
    }
}

internal static class IdentityConfigurationExtensions
{
    /// <summary>
    /// Drops an index EF has already declared, so it can be replaced rather than
    /// duplicated. Silently does nothing if Identity stops declaring it in a future
    /// version — the composite index added alongside is what carries the guarantee, and it
    /// is asserted by a test.
    /// </summary>
    public static void RemoveIndexOn<TEntity>(this EntityTypeBuilder<TEntity> builder, string propertyName)
        where TEntity : class, ITenantOwned
    {
        var property = builder.Metadata.FindProperty(propertyName);

        if (property is null)
        {
            return;
        }

        var index = builder.Metadata.FindIndex(property);

        if (index is not null)
        {
            builder.Metadata.RemoveIndex(index);
        }
    }
}
