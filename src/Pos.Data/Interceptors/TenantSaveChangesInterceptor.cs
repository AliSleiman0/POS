using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Pos.Core.Auditing;
using Pos.Core.Exceptions;
using Pos.Core.Tenancy;

namespace Pos.Data.Interceptors;

/// <summary>
/// Stamps tenant and audit columns on insert, and refuses any write that crosses a
/// tenant boundary. The write-side half of layer 2 (docs/ARCHITECTURE.md#multi-tenancy).
/// </summary>
/// <remarks>
/// The query filter already makes another tenant's rows unloadable, so in normal code
/// this never fires. It exists for the paths that get around the filter — an entity
/// attached by id, an object deserialised straight from a request body, or a query run
/// under <c>IgnoreQueryFilters()</c>. Two independent layers, failing independently.
/// </remarks>
public sealed class TenantSaveChangesInterceptor(
    ITenantContext tenantContext,
    ICurrentActor currentActor,
    TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        Stamp(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        // Both overloads, not just the async one. A single synchronous SaveChanges()
        // anywhere — in a seeder, a migration, a test — would otherwise write unstamped,
        // unchecked rows, and nothing would say so.
        Stamp(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void Stamp(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var actorId = currentActor.UserId;

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    StampInsert(entry, now, actorId);
                    break;

                case EntityState.Modified:
                    GuardExistingRow(entry);
                    StampUpdate(entry, now, actorId);
                    break;

                case EntityState.Deleted:
                    GuardExistingRow(entry);
                    break;

                case EntityState.Detached:
                case EntityState.Unchanged:
                default:
                    break;
            }
        }
    }

    private void StampInsert(EntityEntry<ITenantOwned> entry, DateTimeOffset now, Guid? actorId)
    {
        if (tenantContext.IsResolved)
        {
            var ambient = tenantContext.TenantId;

            // A caller that pre-set a *different* tenant is not corrected, it is rejected.
            // Silently overwriting would turn "this request tried to write into another
            // shop" into a successful write with no trace that it was ever attempted.
            if (entry.Entity.TenantId != Guid.Empty && entry.Entity.TenantId != ambient)
            {
                throw new CrossTenantWriteException(entry.Metadata.DisplayName(), entry.Entity.TenantId, ambient);
            }

            entry.Entity.TenantId = ambient;
        }
        else if (entry.Entity.TenantId == Guid.Empty)
        {
            // The system context reaches here. It is allowed to insert tenant-owned rows
            // (provisioning creates the first Owner), but only by naming the tenant
            // explicitly — it never gets a default.
            throw new TenantNotResolvedException(
                $"Cannot insert '{entry.Metadata.DisplayName()}': no ambient tenant is resolved and the " +
                "entity carries no TenantId of its own.");
        }

        if (entry.Entity is TenantEntity tenantEntity)
        {
            if (tenantEntity.Id == Guid.Empty)
            {
                // UUIDv7: time-ordered, so inserts land at the end of the index instead of
                // scattering across it, without exposing a sequential count of the business.
                tenantEntity.Id = Guid.CreateVersion7(now);
            }

            tenantEntity.CreatedAt = now;
            tenantEntity.CreatedBy ??= actorId;
        }
    }

    private static void StampUpdate(EntityEntry<ITenantOwned> entry, DateTimeOffset now, Guid? actorId)
    {
        if (entry.Entity is not TenantEntity tenantEntity)
        {
            return;
        }

        tenantEntity.UpdatedAt = now;
        tenantEntity.UpdatedBy = actorId;

        // "Created" means created. Letting an update rewrite it turns the audit columns
        // into decoration, and they are the first thing anyone reaches for when a row
        // looks wrong.
        entry.Property(nameof(TenantEntity.CreatedAt)).IsModified = false;
        entry.Property(nameof(TenantEntity.CreatedBy)).IsModified = false;
    }

    private void GuardExistingRow(EntityEntry<ITenantOwned> entry)
    {
        var tenantProperty = entry.Property(nameof(ITenantOwned.TenantId));

        // OriginalValue, not the current one: an attacker's move is to load a row and
        // reassign its tenant, which would look perfectly in-tenant if we only checked
        // where it is going.
        var owner = (Guid)(tenantProperty.OriginalValue ?? Guid.Empty);

        if (!tenantContext.IsResolved)
        {
            throw new TenantNotResolvedException(
                $"Cannot modify or delete '{entry.Metadata.DisplayName()}' with no ambient tenant resolved.");
        }

        var ambient = tenantContext.TenantId;

        if (owner != ambient)
        {
            throw new CrossTenantWriteException(entry.Metadata.DisplayName(), owner, ambient);
        }

        if (entry.State == EntityState.Modified && (Guid)(tenantProperty.CurrentValue ?? Guid.Empty) != owner)
        {
            throw new CrossTenantWriteException(
                $"Refusing to move '{entry.Metadata.DisplayName()}' from tenant '{owner}' to " +
                $"'{tenantProperty.CurrentValue}'. Rows do not change owner.");
        }
    }
}
