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
    /// Appends several movements inside a transaction the <b>caller</b> owns.
    /// </summary>
    /// <remarks>
    /// This is how the sale path joins the ledger rather than reimplementing it. A sale writes
    /// its lines, its tenders and its stock movements together or not at all, so the ledger
    /// cannot be the one deciding when to commit.
    /// <para>
    /// <b>It does not begin, commit or roll back, and it throws if no transaction is open.</b>
    /// The guard is the point: the entire value of this method over
    /// <see cref="RecordAsync"/> is that its writes live and die with the caller's, and a
    /// caller who forgot to open one would otherwise get an ambient auto-commit per statement
    /// and discover it when a half-written sale survived a failure.
    /// </para>
    /// <para>
    /// One <c>SaveChanges</c> for the whole batch, so the concurrency token still applies and
    /// a lost race is still reported. One timestamp for the whole batch too: every movement of
    /// one sale shares that sale's instant, which is what a ledger read expects. Two requests
    /// for the same product produce <b>two</b> movements and one net change to the on-hand —
    /// an item scanned twice is two honest ledger rows.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">No transaction is open on the caller's context.</exception>
    /// <exception cref="Exceptions.ConcurrentStockUpdateException">
    /// Another writer changed one of these products' stock first.
    /// </exception>
    Task<IReadOnlyList<StockMovementResult>> RecordBatchAsync(
        IReadOnlyList<StockMovementRequest> requests,
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
