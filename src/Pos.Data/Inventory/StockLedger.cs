using Microsoft.EntityFrameworkCore;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Inventory;

namespace Pos.Data.Inventory;

/// <summary>
/// The EF implementation of <see cref="IStockLedger"/>.
/// </summary>
/// <remarks>
/// This class exists so that "a movement and the cached total move together, or neither
/// does" is written once. Phase 3's sale path needs the same guarantee at a point where it
/// is also writing a sale, its lines and its tender, and a second copy of the rule would be
/// a second chance to get it subtly different.
/// <para>
/// <b>Everything here runs inside the execution strategy.</b> The connection is configured
/// with <c>EnableRetryOnFailure</c>, and a retrying strategy refuses a user-initiated
/// transaction outright — it cannot replay a block it does not own. Wrapping the whole unit
/// is what lets the retry and the transaction coexist; see
/// <c>TaxClassEndpoints.SaveWithDefaultAsync</c>, which met the same wall first.
/// </para>
/// </remarks>
internal sealed class StockLedger(
    AppDbContext db,
    TimeProvider timeProvider,
    ICurrentActor actor) : IStockLedger
{
    public async Task<StockMovementResult> RecordAsync(
        StockMovementRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read here, not inside the retry: a replay of the block must not re-timestamp the
        // movement, or a transient failure would silently move when it happened.
        var occurredAt = timeProvider.GetUtcNow();
        var performedBy = actor.UserId;

        StockMovementResult? result = null;

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var written = await AppendAsync([request], occurredAt, performedBy, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            result = written[0];
        });

        return result!;
    }

    public async Task<IReadOnlyList<StockMovementResult>> RecordBatchAsync(
        IReadOnlyList<StockMovementRequest> requests,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);

        // The guard is the whole point of this method existing separately. Its value over
        // RecordAsync is that the writes live and die with the caller's transaction, so
        // running without one is not a degraded mode to tolerate — it is a caller who will
        // discover the mistake when a half-written sale survives a failure.
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{nameof(RecordBatchAsync)} must be called inside a transaction the caller owns. "
                + $"Use {nameof(RecordAsync)} for a standalone movement.");
        }

        return await AppendAsync(
            requests,
            timeProvider.GetUtcNow(),
            actor.UserId,
            cancellationToken);
    }

    /// <summary>
    /// The rule itself: append the movements and move the cached totals with them, in one save.
    /// </summary>
    /// <remarks>
    /// Written once and reached through two entry points that differ only in who owns the
    /// transaction. A second copy for the batch case would be a second chance to get "the
    /// movement and the total move together" subtly different, which is the exact failure this
    /// class exists to prevent.
    /// </remarks>
    private async Task<IReadOnlyList<StockMovementResult>> AppendAsync(
        IReadOnlyList<StockMovementRequest> requests,
        DateTimeOffset occurredAt,
        Guid? performedBy,
        CancellationToken cancellationToken)
    {
        var productIds = requests.Select(r => r.ProductId).Distinct().ToArray();

        var stockItems = await db.StockItems
            .Where(s => productIds.Contains(s.ProductId))
            .ToDictionaryAsync(s => s.ProductId, cancellationToken);

        var movements = new StockMovement[requests.Count];

        for (var index = 0; index < requests.Count; index++)
        {
            var request = requests[index];

            if (!stockItems.TryGetValue(request.ProductId, out var stock))
            {
                // The first receipt of a new product is the ordinary way a stock row comes
                // into existence. Refusing it would make "receive stock" fail on exactly the
                // products a shop had just added.
                stock = new StockItem { ProductId = request.ProductId, OnHand = 0m };
                db.StockItems.Add(stock);
                stockItems[request.ProductId] = stock;
            }

            // Two requests for one product resolve to the same tracked row, so the on-hand
            // moves once by the net amount while both movements are still written. An item
            // scanned twice is two honest ledger rows.
            stock.OnHand += request.Quantity;

            movements[index] = new StockMovement
            {
                ProductId = request.ProductId,
                Type = request.Type,
                Quantity = request.Quantity,
                Reason = request.Reason,
                SaleId = request.SaleId,
                PerformedBy = performedBy,
                OccurredAt = occurredAt,
            };

            db.StockMovements.Add(movements[index]);
        }

        try
        {
            // One save, so the movements and the totals are one statement batch. Two saves
            // would still be atomic inside the transaction, but would leave a window in which
            // the invariant is false to anything reading through the same context.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // StockItem.RowVersion is Postgres' xmin, so this is the database reporting that
            // another writer changed the row between the read above and the write — not a
            // guess. Not retried here: see the exception's remarks.
            throw new ConcurrentStockUpdateException(
                "Another change to this product's stock landed first. Re-read it and try again.",
                exception);
        }

        return [.. movements.Select(m => new StockMovementResult(
            m.Id,
            stockItems[m.ProductId].OnHand,
            m.OccurredAt))];
    }

    public async Task<decimal> RebuildOnHandAsync(Guid productId, CancellationToken cancellationToken)
    {
        var rebuilt = await RebuildAsync([productId], cancellationToken);

        return rebuilt.TryGetValue(productId, out var onHand) ? onHand : 0m;
    }

    public async Task<int> RebuildAllOnHandAsync(CancellationToken cancellationToken)
    {
        var productIds = await db.StockItems
            .Select(s => s.ProductId)
            .ToListAsync(cancellationToken);

        // Products that have movements but no stock row yet would otherwise be missed — they
        // are exactly the case a rebuild is for, since a lost stock row is one of the ways
        // the invariant breaks.
        var moved = await db.StockMovements
            .Select(m => m.ProductId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var before = await OnHandByProductAsync(cancellationToken);

        var rebuilt = await RebuildAsync([.. productIds.Union(moved)], cancellationToken);

        return rebuilt.Count(pair =>
            !before.TryGetValue(pair.Key, out var previous) || previous != pair.Value);
    }

    /// <summary>
    /// Recomputes on-hand for the given products from their movements and stores the result.
    /// </summary>
    /// <remarks>
    /// The sum is done in the database — a rebuild over a year of trading is not something to
    /// materialise into memory — but the write goes through tracked entities and
    /// <c>SaveChanges</c>, so the interceptor stamps the tenant and the audit columns, and so
    /// the <c>xmin</c> concurrency token still applies. A rebuild that quietly overwrote a
    /// movement landing at the same moment would be a rebuild that caused drift.
    /// </remarks>
    private async Task<Dictionary<Guid, decimal>> RebuildAsync(
        IReadOnlyList<Guid> productIds,
        CancellationToken cancellationToken)
    {
        var totals = await db.StockMovements
            .Where(m => productIds.Contains(m.ProductId))
            .GroupBy(m => m.ProductId)
            .Select(group => new { ProductId = group.Key, OnHand = group.Sum(m => m.Quantity) })
            .ToDictionaryAsync(row => row.ProductId, row => row.OnHand, cancellationToken);

        var stockItems = await db.StockItems
            .Where(s => productIds.Contains(s.ProductId))
            .ToListAsync(cancellationToken);

        var rebuilt = new Dictionary<Guid, decimal>();

        foreach (var productId in productIds)
        {
            // No movements at all means zero, not "leave it alone". The ledger is the truth,
            // and an empty ledger says the shop holds none of it.
            var onHand = totals.GetValueOrDefault(productId, 0m);

            var stock = stockItems.Find(s => s.ProductId == productId);

            if (stock is null)
            {
                stock = new StockItem { ProductId = productId, OnHand = onHand };
                db.StockItems.Add(stock);
            }
            else
            {
                stock.OnHand = onHand;
            }

            rebuilt[productId] = onHand;
        }

        await db.SaveChangesAsync(cancellationToken);

        return rebuilt;
    }

    private Task<Dictionary<Guid, decimal>> OnHandByProductAsync(CancellationToken cancellationToken) =>
        db.StockItems
            .AsNoTracking()
            .ToDictionaryAsync(s => s.ProductId, s => s.OnHand, cancellationToken);
}
