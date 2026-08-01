using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Sales;

/// <summary>One payment to record against the sale.</summary>
/// <remarks>
/// <c>ChangeGiven</c> is not here: the writer computes it, because it is a function of the
/// total and the tenders together and a caller that could supply it could record a drawer
/// that never balanced.
/// </remarks>
public sealed record TenderInstruction(TenderMethod Method, Money Amount, string? Reference = null);

/// <summary>Everything needed to commit one sale.</summary>
/// <remarks>
/// The amounts arrive already priced. Pricing is pure and depends only on the cart as it was
/// read, so doing it outside the transaction is correct — and it is what lets
/// <c>POST /sales/quote</c> and <c>POST /sales</c> produce the same numbers from the same call.
/// <para>
/// <b>There is no cashier id here, deliberately.</b> The writer takes it from the current
/// actor, the way the stock ledger takes <c>PerformedBy</c>: a caller able to supply it could
/// attribute a sale — and a price override — to a colleague, and nothing on the row would say
/// otherwise.
/// </para>
/// </remarks>
/// <param name="ClientTransactionId">The client's GUID for this cart. Unique per tenant.</param>
/// <param name="RegisterId">The till. Must be the shift's own register.</param>
/// <param name="ShiftId">The open shift the money goes into.</param>
/// <param name="TaxMode">Snapshotted onto the sale, so a report never re-reads the tenant.</param>
/// <param name="Priced">The computed amounts and lines.</param>
/// <param name="Tenders">What was handed over.</param>
/// <param name="StockTrackedProductIds">
/// Which of the cart's products decrement stock. Resolved by the caller from the catalog,
/// because the ledger has no opinion about <c>TrackStock</c> and should not grow one.
/// </param>
public sealed record SaleCommitRequest(
    Guid ClientTransactionId,
    Guid RegisterId,
    Guid ShiftId,
    TaxMode TaxMode,
    PricedSale Priced,
    IReadOnlyList<TenderInstruction> Tenders,
    IReadOnlySet<Guid> StockTrackedProductIds);

/// <summary>What was committed.</summary>
/// <param name="SaleId">The new sale.</param>
/// <param name="SaleNumber">The per-tenant sequential reference a person quotes.</param>
/// <param name="CompletedAt">Server-set, from <c>TimeProvider</c>.</param>
/// <param name="ChangeGiven">The excess over the total, handed back.</param>
/// <param name="LineIds">The persisted line ids, in cart order.</param>
/// <param name="Discrepancies">Products whose on-hand went negative, flagged for review.</param>
public sealed record SaleCommitResult(
    Guid SaleId,
    long SaleNumber,
    DateTimeOffset CompletedAt,
    Money ChangeGiven,
    IReadOnlyList<Guid> LineIds,
    IReadOnlyList<Guid> Discrepancies);

/// <summary>
/// Commits a sale: its number, its lines, its tenders, its stock movements and its
/// idempotency record, in one transaction.
/// </summary>
/// <remarks>
/// <b>The second persistence port Core declares</b>, and it earns one on the same grounds
/// <c>IStockLedger</c> did: this is not a save. It is a transaction containing a row lock, a
/// counter increment, a concurrency token and a batch append, and the guarantee it provides —
/// all of it lands or none of it does — cannot be expressed as "add some entities".
/// <para>
/// A port per repository is still <b>not</b> being adopted. The reading endpoints use
/// <c>AppDbContext</c> directly, as they have since Phase 2.
/// </para>
/// </remarks>
public interface ISaleWriter
{
    /// <summary>Commits <paramref name="request"/>, or nothing at all.</summary>
    /// <param name="request">The sale to write.</param>
    /// <param name="onCommitting">
    /// Invoked inside the transaction once the result is known and before it commits, so the
    /// caller can enlist further writes — in practice the idempotency record, which the
    /// contract requires to land with the work or not at all.
    /// <para>
    /// A callback rather than a dependency, because <c>Pos.Data</c> cannot reference the API's
    /// idempotency types and a host with no HTTP — the seeder, a maintenance job — has no key
    /// to record. It adds entities to the caller's own context; the writer saves them.
    /// </para>
    /// </param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <exception cref="Exceptions.ShiftClosedException">The shift is not open.</exception>
    /// <exception cref="Exceptions.ConcurrentStockUpdateException">
    /// Another writer changed one of the products' stock first.
    /// </exception>
    Task<SaleCommitResult> CommitAsync(
        SaleCommitRequest request,
        Action<SaleCommitResult>? onCommitting,
        CancellationToken cancellationToken);
}
