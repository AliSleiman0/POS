using Pos.Core.Entities;

namespace Pos.Core.Inventory;

/// <summary>What a caller asks the ledger to record. Validated before it gets here.</summary>
/// <remarks>
/// Deliberately not a <see cref="StockMovement"/>. <c>OccurredAt</c> and <c>PerformedBy</c>
/// are the ledger's to set, from <c>TimeProvider</c> and the current actor — a caller that
/// could supply them could backdate a write-off or attribute it to somebody else, and there
/// would be no way to tell from the row.
/// </remarks>
public sealed record StockMovementRequest(
    Guid ProductId,
    StockMovementType Type,
    decimal Quantity,
    string? Reason,
    Guid? SaleId = null);

/// <summary>What was written, and where it left the on-hand figure.</summary>
/// <remarks>
/// <c>OnHand</c> is returned rather than left to a follow-up read: the caller has just
/// changed it, a re-read would race with the next writer, and the number is already in hand
/// inside the transaction that produced it.
/// </remarks>
public sealed record StockMovementResult(Guid MovementId, decimal OnHand, DateTimeOffset OccurredAt);

/// <summary>
/// Appends to the stock ledger and keeps <see cref="StockItem.OnHand"/> in step with it.
/// </summary>
/// <remarks>
/// <b>The first persistence port Core declares</b>, and the reason it earns one is that the
/// operation is not a save — it is a transaction with a concurrency token in it, and one that
/// Phase 3's sale path has to join rather than reimplement. Declaring it here keeps
/// <c>Pos.Core</c> free of EF (invariant 1) while letting the rule "a movement and the cached
/// total move together, or neither does" live in one place.
/// <para>
/// <b>The invariant every implementation owes:</b>
/// <c>StockItem.OnHand == SUM(StockMovement.Quantity)</c> for that product, always. Any drift
/// is a bug — and is detectable precisely because the ledger exists, which is what
/// <see cref="RebuildOnHandAsync"/> is for.
/// </para>
/// </remarks>
public interface IStockLedger
{
    /// <summary>
    /// Appends one movement and applies it to the cached on-hand, atomically.
    /// </summary>
    /// <remarks>
    /// Creates the <see cref="StockItem"/> when the product has none: the first receipt of a
    /// new product is the ordinary way a stock row comes into existence, and refusing it
    /// would make "receive stock" fail on exactly the products a shop had just added.
    /// </remarks>
    /// <exception cref="Exceptions.ConcurrentStockUpdateException">
    /// Another writer changed the same product's stock first.
    /// </exception>
    Task<StockMovementResult> RecordAsync(
        StockMovementRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// Recomputes one product's on-hand from its movements and stores the result.
    /// </summary>
    /// <returns>The recomputed on-hand.</returns>
    /// <remarks>
    /// The proof that the ledger is the truth and the number is derived — and what you run
    /// when the invariant has drifted. It is not exposed over HTTP: "rewrite the stock
    /// figures" needs a decision about who may do it that Phase 2 does not need to take.
    /// </remarks>
    Task<decimal> RebuildOnHandAsync(Guid productId, CancellationToken cancellationToken);

    /// <summary>
    /// Rebuilds every product's on-hand in the current tenant.
    /// </summary>
    /// <returns>How many rows disagreed with their ledger and were corrected.</returns>
    Task<int> RebuildAllOnHandAsync(CancellationToken cancellationToken);
}
