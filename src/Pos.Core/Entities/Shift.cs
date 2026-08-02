using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// One register's trading period, opened with a float and closed against a physical count.
/// </summary>
/// <remarks>
/// Without shifts, "the drawer is €12 short" is unanswerable — there is nothing to reconcile
/// against. The variance this produces is the single report an owner checks daily, which is
/// why a sale requires an open shift rather than treating one as optional bookkeeping.
/// <para>
/// At most one open shift per register, enforced by a filtered unique index rather than a
/// check-then-insert, so two tills opening at once leaves one with a 409 instead of two open
/// drawers nobody can reconcile.
/// </para>
/// </remarks>
public sealed class Shift : TenantEntity
{
    public Guid RegisterId { get; set; }

    public Guid OpenedBy { get; set; }

    public DateTimeOffset OpenedAt { get; set; }

    /// <summary>The cash in the drawer at open.</summary>
    public Money OpeningFloat { get; set; }

    /// <inheritdoc cref="ShiftStatus" />
    public ShiftStatus Status { get; set; } = ShiftStatus.Open;

    public Guid? ClosedBy { get; set; }

    public DateTimeOffset? ClosedAt { get; set; }

    /// <summary>What was physically counted at close.</summary>
    public Money? CountedCash { get; set; }

    /// <summary>
    /// What should have been there: float + cash sales − cash refunds + cash movements.
    /// </summary>
    /// <remarks>
    /// Stored rather than recomputed on read. Recomputing it later would silently change a
    /// historical variance whenever anything about the underlying sales changed — which is
    /// the same class of mistake as joining a report to the current product price.
    /// </remarks>
    public Money? ExpectedCash { get; set; }

    /// <summary>
    /// <see cref="CountedCash"/> − <see cref="ExpectedCash"/>. Negative means short.
    /// </summary>
    public Money? Variance { get; set; }
}
