using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pos.Core.Auditing;
using Pos.Core.Entities;

namespace Pos.Data.Auditing;

/// <summary>
/// The EF implementation of <see cref="IAuditLog"/>.
/// </summary>
/// <remarks>
/// <b>Staging is the whole mechanism.</b> <c>AppDbContext</c> is scoped, and every writer in
/// the application — <c>SaleWriter</c>, <c>StockLedger</c>, <c>ShiftWriter</c>, and
/// <c>UserManager</c> through <c>AddEntityFrameworkStores</c> — resolves the same instance
/// within a request. So an entry added here joins whatever transaction is already open on
/// that context and commits or rolls back with it, with nothing for the call site to
/// co-ordinate. That is what makes "an audit entry cannot be missing for an action that
/// succeeded, nor present for one that rolled back" true rather than aspirational.
/// </remarks>
internal sealed class AuditLog(
    AppDbContext db,
    ICurrentActor actor,
    TimeProvider timeProvider) : IAuditLog
{
    /// <remarks>
    /// Deliberately plain. The payloads are flat string dictionaries, so there is nothing to
    /// configure and no reason to share the HTTP layer's options — a change to how the API
    /// renders responses must not silently change what was written into history.
    /// </remarks>
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public void Record(
        AuditAction action,
        string entityType,
        Guid entityId,
        IReadOnlyDictionary<string, string?>? before = null,
        IReadOnlyDictionary<string, string?>? after = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entityType);

        var entry = new AuditEntry
        {
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Before = Serialize(before),
            After = Serialize(after),
            ActorId = actor.UserId,
            RegisterId = actor.RegisterId,
            OccurredAt = timeProvider.GetUtcNow(),
        };

        // The sale and stock writers run inside CreateExecutionStrategy().ExecuteAsync, and a
        // transient failure replays that block with the change tracker still holding what the
        // failed attempt added — so the onCommitting callback fires again and stages a second
        // entry. The idempotency record has a unique index that would catch its own version of
        // this loudly; an audit entry has nothing, so it would commit a silent duplicate.
        if (IsAlreadyStaged(entry))
        {
            return;
        }

        db.AuditEntries.Add(entry);
    }

    public async Task RecordStandaloneAsync(
        AuditAction action,
        string entityType,
        Guid entityId,
        IReadOnlyDictionary<string, string?>? before = null,
        IReadOnlyDictionary<string, string?>? after = null,
        CancellationToken cancellationToken = default)
    {
        Record(action, entityType, entityId, before, after);

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <remarks>
    /// Compares what was passed in rather than identity, because the replayed call constructs
    /// a second object describing the same event. <c>OccurredAt</c> is excluded: the retry
    /// re-reads the clock, so including it would make every duplicate look distinct, which is
    /// exactly the case this exists to catch.
    /// </remarks>
    private bool IsAlreadyStaged(AuditEntry candidate) =>
        db.ChangeTracker
            .Entries<AuditEntry>()
            .Where(e => e.State == EntityState.Added)
            .Select(e => e.Entity)
            .Any(existing =>
                existing.Action == candidate.Action
                && existing.EntityId == candidate.EntityId
                && string.Equals(existing.EntityType, candidate.EntityType, StringComparison.Ordinal)
                && string.Equals(existing.Before, candidate.Before, StringComparison.Ordinal)
                && string.Equals(existing.After, candidate.After, StringComparison.Ordinal));

    private static string? Serialize(IReadOnlyDictionary<string, string?>? payload) =>
        payload is null || payload.Count == 0
            ? null
            : JsonSerializer.Serialize(payload, PayloadOptions);
}
