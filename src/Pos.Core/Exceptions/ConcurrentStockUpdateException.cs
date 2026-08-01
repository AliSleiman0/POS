namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when two writers moved the same product's stock at once and this one lost.
/// </summary>
/// <remarks>
/// The optimistic concurrency token on <c>StockItem</c> is Postgres' <c>xmin</c>, so the loss
/// is detected by the database rather than guessed at. A 409, like the other conflicts:
/// nothing about the request is wrong, and re-reading and retrying is a meaningful thing for
/// the caller to do.
/// <para>
/// <b>Not retried internally, deliberately.</b> A silent retry would re-apply a delta the
/// caller may already have applied — the difference between "receive six" attempted twice and
/// twelve arriving is invisible from inside the ledger. Phase 3.5's idempotency keys are what
/// make an automatic retry safe; until then the decision belongs to whoever knows whether the
/// stock actually moved.
/// </para>
/// </remarks>
public sealed class ConcurrentStockUpdateException : PosDomainException
{
    public ConcurrentStockUpdateException()
        : base("Another change to this product's stock landed first. Re-read it and try again.")
    {
    }

    public ConcurrentStockUpdateException(string message)
        : base(message)
    {
    }

    public ConcurrentStockUpdateException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "concurrent-stock-update";
}
