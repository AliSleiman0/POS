using Microsoft.EntityFrameworkCore;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Shifts;
using Pos.Core.Tenancy;

namespace Pos.Data.Shifts;

/// <summary>What a close worked out.</summary>
public sealed record ShiftCloseResult(
    Guid ShiftId,
    DateTimeOffset ClosedAt,
    Money CountedCash,
    Money ExpectedCash,
    Money Variance);

/// <summary>
/// Closes a shift against a physical count, and decides the race with any sale in flight.
/// </summary>
/// <remarks>
/// <b>Not a Core port, unlike the sale writer.</b> There is no rule here Core needs to own:
/// the arithmetic is already pure in <see cref="ShiftArithmetic"/>, and what remains is three
/// queries and a lock. Declaring a port for it would be adopting the repository pattern that
/// <c>DECISIONS.md</c> explicitly did not adopt.
/// <para>
/// Public rather than internal-behind-an-interface for the same reason: <c>Pos.Api</c>
/// references <c>Pos.Data</c> and its endpoints already use <c>AppDbContext</c> directly. A
/// port would exist only to hide a type from a project that is allowed to see it.
/// </para>
/// </remarks>
public sealed class ShiftWriter(
    AppDbContext db,
    ITenantContext tenant,
    ICurrentActor actor,
    TimeProvider timeProvider)
{
    public async Task<ShiftCloseResult> CloseAsync(
        Guid shiftId,
        Money countedCash,
        Action<ShiftCloseResult>? onCommitting,
        CancellationToken cancellationToken)
    {
        var closedAt = timeProvider.GetUtcNow();
        var closedBy = actor.UserId
            ?? throw new InvalidOperationException("No user is attached to this request.");

        ShiftCloseResult? result = null;

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // The exclusive half of the pair that makes a concurrent sale deterministic.
            //
            // SaleWriter takes FOR SHARE on this row before it writes anything, so the two
            // serialise: either the sale commits first and this close counts it, or the sale
            // blocks here, then finds the shift closed and is refused. No sale is ever
            // counted-then-refused, and none is committed-but-uncounted.
            //
            // The status is changed as part of the SAME statement that takes the lock, so
            // there is no window between checking and setting it. A second close finds no
            // 'Open' row and gets nothing back.
            var locked = await db.Database
                .SqlQuery<Guid>(
                    $"""
                     UPDATE shift SET status = 'Closed'
                     WHERE tenant_id = {tenant.TenantId} AND id = {shiftId} AND status = 'Open'
                     RETURNING id AS "Value"
                     """)
                .ToListAsync(cancellationToken);

            if (locked.Count == 0)
            {
                throw new ShiftClosedException();
            }

            var expected = ShiftArithmetic.ExpectedCash(
                await OpeningFloatAsync(shiftId, cancellationToken),
                await NetCashTenderedAsync(shiftId, cancellationToken),
                await CashMovementTotalAsync(shiftId, cancellationToken));

            var variance = ShiftArithmetic.Variance(countedCash, expected);

            // Read after the UPDATE above, so the tracked entity carries the new status and
            // EF does not write 'Open' back over it.
            var shift = await db.Shifts.FirstAsync(s => s.Id == shiftId, cancellationToken);

            shift.Status = ShiftStatus.Closed;
            shift.ClosedAt = closedAt;
            shift.ClosedBy = closedBy;
            shift.CountedCash = countedCash;
            shift.ExpectedCash = expected;
            shift.Variance = variance;

            result = new ShiftCloseResult(shiftId, closedAt, countedCash, expected, variance);

            onCommitting?.Invoke(result);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return result!;
    }

    /// <summary>
    /// Cash tendered less change given, over this shift's non-voided sales and refunds.
    /// </summary>
    /// <remarks>
    /// Raw SQL because <b>EF cannot aggregate a value-converted property</b> — <c>Money</c> is
    /// a struct over <c>decimal</c>, and <c>SumAsync</c> over it does not translate. Recorded
    /// in DECISIONS.md as the known cost of typing entity amounts as <c>Money</c>, and pinned
    /// by <c>MoneyMappingTests.Summing_money_in_the_database_is_not_translatable</c>.
    /// <para>
    /// The <c>tenant_id</c> predicate is written by hand, because the global query filter
    /// composes over LINQ and not over this. That is not a bypass of invariant 2 — the tenant
    /// comes from the validated token exactly as everywhere else, and row-level security is
    /// underneath as the layer that holds when application code is wrong.
    /// </para>
    /// <para>
    /// <b>Voided sales are excluded rather than netted off.</b> A void hands the cash straight
    /// back, so it never stayed in the drawer. Only <c>Cash</c> tenders count: an
    /// <c>External</c> terminal's takings reconcile against that terminal, not against this
    /// drawer.
    /// </para>
    /// </remarks>
    private async Task<Money> NetCashTenderedAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        var total = await db.Database
            .SqlQuery<decimal>(
                $"""
                 SELECT COALESCE(SUM(t.amount - COALESCE(t.change_given, 0)), 0) AS "Value"
                 FROM tender t
                 JOIN sale s ON s.id = t.sale_id AND s.tenant_id = t.tenant_id
                 WHERE t.tenant_id = {tenant.TenantId}
                   AND s.shift_id = {shiftId}
                   AND s.status <> 'Voided'
                   AND t.method = 'Cash'
                 """)
            .FirstAsync(cancellationToken);

        return (Money)total;
    }

    private async Task<Money> CashMovementTotalAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        var total = await db.Database
            .SqlQuery<decimal>(
                $"""
                 SELECT COALESCE(SUM(amount), 0) AS "Value"
                 FROM cash_movement
                 WHERE tenant_id = {tenant.TenantId} AND shift_id = {shiftId}
                 """)
            .FirstAsync(cancellationToken);

        return (Money)total;
    }

    private async Task<Money> OpeningFloatAsync(Guid shiftId, CancellationToken cancellationToken)
    {
        var amount = await db.Database
            .SqlQuery<decimal>(
                $"""
                 SELECT opening_float AS "Value" FROM shift
                 WHERE tenant_id = {tenant.TenantId} AND id = {shiftId}
                 """)
            .FirstAsync(cancellationToken);

        return (Money)amount;
    }
}
