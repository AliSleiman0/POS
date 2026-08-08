using Pos.Core.Entities;

namespace Pos.Core.Auditing;

/// <summary>
/// Writes the append-only record of actions that move money. The port Core declares;
/// <c>Pos.Data</c> implements it.
/// </summary>
/// <remarks>
/// <b>The contract is that an entry cannot exist for an action that rolled back, nor be
/// missing for one that succeeded.</b> That is achieved by staging rather than saving:
/// <see cref="Record"/> adds the entry to the same unit of work the caller is already in, so
/// whatever transaction commits the audited action commits its entry, and whatever rolls one
/// back rolls back the other. There is nothing to co-ordinate and nothing to get wrong at the
/// call site beyond calling it inside the transaction.
/// <para>
/// The two methods are named differently rather than overloaded on purpose.
/// <see cref="Record"/> always means "my caller saves"; the only situation with no
/// transaction to join is a refusal, which is what
/// <see cref="RecordStandaloneAsync"/> is for. A staged-but-never-saved entry is then only
/// reachable by calling <see cref="Record"/> in a handler that never saves, which the
/// per-action tests catch immediately.
/// </para>
/// <para>
/// Note the deliberate difference from <c>IIdempotencyContext.Record</c>, which takes the
/// <c>DbContext</c> as a parameter. That interface lives in <c>Pos.Api</c> and cannot be
/// injected into <c>Pos.Data</c>'s writers; this one lives in Core, so the implementation
/// injects the context and the port stays free of EF (CLAUDE.md invariant 1).
/// </para>
/// </remarks>
public interface IAuditLog
{
    /// <summary>
    /// Stages an entry on the caller's unit of work. <b>The caller saves.</b>
    /// </summary>
    /// <param name="action">What happened.</param>
    /// <param name="entityType">What it happened to — use <c>nameof</c>.</param>
    /// <param name="entityId">Which one.</param>
    /// <param name="before">State before, or null when the action created something.</param>
    /// <param name="after">State after, or null when the action removed something.</param>
    void Record(
        AuditAction action,
        string entityType,
        Guid entityId,
        IReadOnlyDictionary<string, string?>? before = null,
        IReadOnlyDictionary<string, string?>? after = null);

    /// <summary>
    /// Stages an entry <b>and saves it</b>. For actions with no transaction to join —
    /// in practice, refusals, where the work being audited is the fact that no work happened.
    /// </summary>
    /// <remarks>
    /// Saves the caller's whole unit of work, not just this entry, so it must only be called
    /// where nothing else is tracked. Every current call site sits on a read path that is
    /// <c>AsNoTracking</c> throughout.
    /// </remarks>
    Task RecordStandaloneAsync(
        AuditAction action,
        string entityType,
        Guid entityId,
        IReadOnlyDictionary<string, string?>? before = null,
        IReadOnlyDictionary<string, string?>? after = null,
        CancellationToken cancellationToken = default);
}
