using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// Cash into or out of a drawer other than through a sale. Append-only.
/// </summary>
/// <remarks>
/// Every one of these changes what the drawer should hold at close, so the shift's expected
/// cash is float + cash sales − cash refunds + the sum of these.
/// </remarks>
public sealed class CashMovement : TenantEntity
{
    public const int ReasonMaxLength = 200;

    public Guid ShiftId { get; set; }

    /// <inheritdoc cref="CashMovementType" />
    public CashMovementType Type { get; set; }

    /// <summary>
    /// Signed, and checked against <see cref="Type"/> before it is written.
    /// </summary>
    /// <remarks>
    /// Signed rather than a magnitude with the direction implied by the type, so expected
    /// cash is a <c>SUM</c> and not a fold that has to know what every type means — the same
    /// reasoning as <c>StockMovement.Quantity</c>. No check constraint can police the sign
    /// against the type, because both are perfectly good numbers for the column; a drop
    /// entered as a positive is a drawer wrong by twice the amount, in the direction nobody
    /// notices until close.
    /// </remarks>
    public Money Amount { get; set; }

    /// <summary>
    /// Required. A cash movement with no reason is the record you need six months later and
    /// will not have — the same rule, for the same reason, as a stock adjustment's.
    /// </summary>
    public required string Reason { get; set; }

    public Guid PerformedBy { get; set; }

    /// <summary>Server-set from <c>TimeProvider</c>, never client-supplied.</summary>
    public DateTimeOffset OccurredAt { get; set; }
}
