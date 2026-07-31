namespace Pos.Core.Auditing;

/// <summary>
/// Who is performing the current unit of work, for the <c>CreatedBy</c>/<c>UpdatedBy</c>
/// stamps and, from Phase 7, the audit log.
/// </summary>
/// <remarks>
/// Separate from <see cref="Tenancy.ITenantContext"/> on purpose: plenty of legitimate
/// work has a tenant but no user (provisioning, a background job, a replayed offline
/// sale), and conflating the two would either invent a user or block the work.
/// </remarks>
public interface ICurrentActor
{
    /// <summary>The acting user, or <see langword="null"/> when the system itself is acting.</summary>
    Guid? UserId { get; }
}

/// <summary>
/// The actor for work with no user behind it. <c>CreatedBy</c> stays null, which reads as
/// "the system did this" rather than misattributing it to whoever happened to trigger it.
/// </summary>
public sealed class SystemActor : ICurrentActor
{
    public Guid? UserId => null;
}
