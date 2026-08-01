using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Inventory;
using Pos.Core.Monetary;
using Pos.Core.Sales;
using Pos.Core.Tenancy;
using Pos.Core.Tenders;

namespace Pos.Data.Sales;

/// <summary>
/// The EF implementation of <see cref="ISaleWriter"/>: one transaction, or nothing.
/// </summary>
/// <remarks>
/// The order of operations inside the transaction is deliberate and is documented step by step
/// below. The two that would be easy to get wrong are the shift lock, which has to be taken
/// <i>before</i> anything is written, and the principals-before-dependents save ordering,
/// which is forced by there being no navigation properties in this model — EF does no
/// foreign-key fixup, so a line added alongside its sale would carry <c>Guid.Empty</c>.
/// </remarks>
internal sealed class SaleWriter(
    AppDbContext db,
    IStockLedger ledger,
    ITenantContext tenant,
    ICurrentActor actor,
    TimeProvider timeProvider) : ISaleWriter
{
    public async Task<SaleCommitResult> CommitAsync(
        SaleCommitRequest request,
        Action<SaleCommitResult>? onCommitting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Read before the retry block, as StockLedger does: a transient-failure replay must
        // not re-timestamp the sale, or "what did we take on Tuesday" would move.
        var completedAt = timeProvider.GetUtcNow();

        // From the validated token, never the request. A caller able to supply this could
        // attribute a sale — and a price override — to a colleague, and nothing on the row
        // would say otherwise. Same rule as the stock ledger's PerformedBy.
        var cashierId = actor.UserId
            ?? throw new InvalidOperationException(
                "A sale needs a cashier, and no user is attached to this request.");

        var change = TenderRules.ChangeFor(
            request.Priced.Total,
            [.. request.Tenders.Select(t => t.Amount)]);

        SaleCommitResult? result = null;

        // A retrying execution strategy refuses a user-initiated transaction outright, so the
        // whole unit is wrapped in one it owns.
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // 1. Lock the shift, before writing anything at all.
            //
            // FOR SHARE, not FOR UPDATE: many sales may hold this lock at once, and they do
            // not conflict with each other. What they conflict with is the exclusive lock
            // POST /shifts/{id}/close takes, which is the whole point — either this sale
            // commits before the close reads the drawer, or it waits, finds the shift closed
            // and is refused. No sale is ever counted-then-refused or committed-but-uncounted.
            await AssertShiftIsOpenAsync(request, cancellationToken);

            // 2. The sale number, from the per-tenant counter.
            var saleNumber = await NextSaleNumberAsync(completedAt, cancellationToken);

            var sale = new Sale
            {
                SaleNumber = saleNumber,
                ClientTransactionId = request.ClientTransactionId,
                RegisterId = request.RegisterId,
                ShiftId = request.ShiftId,
                CashierId = cashierId,
                Type = SaleType.Sale,
                Status = SaleStatus.Completed,
                TaxMode = request.TaxMode,
                Subtotal = request.Priced.Subtotal,
                DiscountTotal = request.Priced.DiscountTotal,
                TaxTotal = request.Priced.TaxTotal,
                RoundingAdjustment = request.Priced.RoundingAdjustment,
                Total = request.Priced.Total,
                CompletedAt = completedAt,
            };

            db.Sales.Add(sale);

            // 3. Saved on its own, because there are no navigation properties: the interceptor
            //    stamps sale.Id during SaveChanges, and a line added in the same call would
            //    have written Guid.Empty into its foreign key.
            await db.SaveChangesAsync(cancellationToken);

            // 4. Lines and tenders.
            var lines = new SaleLine[request.Priced.Lines.Count];

            for (var index = 0; index < request.Priced.Lines.Count; index++)
            {
                var priced = request.Priced.Lines[index];
                var source = priced.Source;

                lines[index] = new SaleLine
                {
                    SaleId = sale.Id,
                    ProductId = source.ProductId,
                    LineNumber = priced.LineNumber,

                    // The snapshots. Never read from the catalog again — see invariant 5.
                    Description = source.Description,
                    Quantity = source.Quantity,
                    UnitPrice = source.UnitPrice,
                    TaxRate = source.TaxRate,

                    DiscountAmount = priced.Discount,
                    LineSubtotal = priced.Subtotal,
                    LineTax = priced.Tax,
                    LineTotal = priced.Total,

                    IsPriceOverridden = source.IsPriceOverridden,

                    // Until Phase 7.2's audit log exists, these two columns are the whole
                    // record of who authorised an override.
                    OverriddenBy = source.IsPriceOverridden ? cashierId : null,
                };
            }

            db.SaleLines.AddRange(lines);

            foreach (var tender in request.Tenders)
            {
                db.Tenders.Add(new Tender
                {
                    SaleId = sale.Id,
                    Method = tender.Method,
                    Amount = tender.Amount,

                    // Recorded against the first tender rather than spread over them: the
                    // drawer gave the change back once, and attributing a share of it to each
                    // note would be inventing detail nobody observed.
                    ChangeGiven = ReferenceEquals(tender, request.Tenders[0]) && !change.IsZero
                        ? change
                        : null,
                    Reference = tender.Reference,
                });
            }

            await db.SaveChangesAsync(cancellationToken);

            // 5. Stock, through the ledger's batch entry point so the movements and the cached
            //    on-hand move inside THIS transaction rather than one of the ledger's own.
            var movements = request.Priced.Lines
                .Where(line => request.StockTrackedProductIds.Contains(line.Source.ProductId))
                .Select(line => new StockMovementRequest(
                    line.Source.ProductId,
                    StockMovementType.Sale,

                    // Negative: goods leaving the shop. StockRules refuses the other sign.
                    -line.Source.Quantity,

                    // No prose reason. The sale IS the reason, and it is on the row as SaleId;
                    // a reason column full of the word "sale" would be noise in the one table
                    // that exists to be read back.
                    Reason: null,
                    SaleId: sale.Id))
                .ToArray();

            var written = movements.Length > 0
                ? await ledger.RecordBatchAsync(movements, cancellationToken)
                : [];

            // 6. Oversells, flagged rather than refused. The customer is standing there
            //    holding the item; refusing the sale is the wrong behaviour and is how a POS
            //    gets thrown out. Written only where the on-hand ended up BELOW zero — selling
            //    the last three of three lands on zero and is an ordinary sale.
            var discrepancies = new List<Guid>();

            for (var index = 0; index < movements.Length; index++)
            {
                if (written[index].OnHand >= 0m)
                {
                    continue;
                }

                var line = lines.First(l => l.ProductId == movements[index].ProductId);

                var discrepancy = new StockDiscrepancy
                {
                    ProductId = movements[index].ProductId,
                    SaleId = sale.Id,
                    SaleLineId = line.Id,
                    QuantityRequested = -movements[index].Quantity,
                    OnHandAfter = written[index].OnHand,
                    DetectedAt = completedAt,
                };

                db.StockDiscrepancies.Add(discrepancy);
                discrepancies.Add(movements[index].ProductId);
            }

            result = new SaleCommitResult(
                sale.Id,
                saleNumber,
                completedAt,
                change,
                [.. lines.Select(l => l.Id)],
                discrepancies);

            // 7. Whatever the caller wants committed alongside — in practice the idempotency
            //    record, which has to carry the response the caller is about to be given and
            //    has to land inside this transaction or not at all.
            onCommitting?.Invoke(result);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return result!;
    }

    /// <summary>
    /// Takes a share lock on the shift and confirms it is open and owned by this register.
    /// </summary>
    /// <remarks>
    /// Raw SQL because <c>FOR SHARE</c> has no LINQ equivalent, and the tenant predicate is
    /// therefore written by hand: the global query filter composes over LINQ, not over this.
    /// That is not a bypass of invariant 2 — the tenant comes from the validated token exactly
    /// as everywhere else, and row-level security is underneath as the layer that holds when
    /// application code is wrong.
    /// </remarks>
    private async Task AssertShiftIsOpenAsync(SaleCommitRequest request, CancellationToken cancellationToken)
    {
        var tenantId = tenant.TenantId;
        var shiftId = request.ShiftId;

        var registerId = await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT register_id AS "Value" FROM shift
                 WHERE tenant_id = {tenantId} AND id = {shiftId} AND status = 'Open'
                 FOR SHARE
                 """)
            .FirstOrDefaultAsync(cancellationToken);

        if (registerId == Guid.Empty)
        {
            // Either closed or not there. The caller has already checked existence and the
            // register match, so reaching here means it closed in between — which is exactly
            // the race the lock exists to make deterministic.
            throw new ShiftClosedException();
        }

        if (registerId != request.RegisterId)
        {
            // Caught at the endpoint too. Restated here because a sale attributed to the wrong
            // drawer makes a Z-report reconcile the wrong till, and the writer is the last
            // place that can still refuse.
            throw new ShiftClosedException(
                "That shift belongs to a different register.");
        }
    }

    /// <summary>
    /// Takes the next sale number for this tenant, inside the caller's transaction.
    /// </summary>
    /// <remarks>
    /// One statement. <c>ON CONFLICT DO UPDATE</c> takes a row-level exclusive lock, so a
    /// concurrent sale in the same tenant blocks until this one commits and then reads the
    /// committed value — no duplicates, and no <c>SELECT … FOR UPDATE</c> followed by an
    /// <c>UPDATE</c>.
    /// <para>
    /// <b>Not a Postgres sequence.</b> A sequence advances even when the transaction that drew
    /// from it rolls back, so a failed sale would burn a number permanently — and gaps in a
    /// financial series look like deleted records to an auditor, with no way to prove
    /// otherwise. Here the increment rolls back with the sale.
    /// </para>
    /// <para>
    /// Issued through ADO rather than EF because the statement is an upsert with a
    /// <c>RETURNING</c> clause: EF's <c>SqlQuery</c> composes its argument into a subquery,
    /// and Postgres does not allow a data-modifying statement there. The command is enlisted
    /// in the ambient transaction explicitly.
    /// </para>
    /// </remarks>
    private async Task<long> NextSaleNumberAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();

        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            """
            INSERT INTO sale_sequence (id, tenant_id, last_number, created_at)
            VALUES (@id, @tenant, 1, @now)
            ON CONFLICT (tenant_id) DO UPDATE SET last_number = sale_sequence.last_number + 1
            RETURNING last_number
            """;

        AddParameter(command, "id", Guid.CreateVersion7());
        AddParameter(command, "tenant", tenant.TenantId);
        AddParameter(command, "now", now);

        // The INSERT arm is the first sale of a new tenant. There is no onboarding endpoint by
        // decision, so nothing else would ever create this row; an upsert removes the question.
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static void AddParameter(System.Data.Common.DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
