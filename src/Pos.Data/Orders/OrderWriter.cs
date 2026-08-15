using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Tenancy;

namespace Pos.Data.Orders;

/// <summary>Where an order is being opened, and for whom.</summary>
/// <param name="Type">Table, tab or takeaway.</param>
/// <param name="DiningTableId">Required for <see cref="OrderType.Table"/>, null otherwise.</param>
/// <param name="TabName">Required for <see cref="OrderType.Tab"/>.</param>
/// <param name="RegisterId">The till it was opened at, when there was one.</param>
/// <param name="CoverCount">How many are eating. Null for a takeaway.</param>
/// <param name="Note">A note for the floor.</param>
/// <remarks>
/// <b>There is no <c>OpenedBy</c> here, deliberately.</b> The writer takes it from the current
/// actor, exactly as <c>SaleCommitRequest</c> omits the cashier: a caller able to supply it could
/// attribute a table — and every void on it — to a colleague, and nothing on the row would say
/// otherwise.
/// </remarks>
public sealed record OrderOpenRequest(
    OrderType Type,
    Guid? DiningTableId,
    string? TabName,
    Guid? RegisterId,
    int? CoverCount,
    string? Note);

/// <summary>One item to put on an order, already resolved against the catalog.</summary>
/// <remarks>
/// Everything here is a <b>snapshot taken now</b>. The caller looks the product up, applies its
/// tax class, checks whether an override was permitted, and hands the result over — the same
/// division of labour <c>Pos.Core.Pricing.CartLine</c> uses, and for the same reason: the thing
/// that decides amounts must not read the database.
/// </remarks>
/// <param name="Modifiers">
/// Child lines hanging off this one. One level deep, by rule — a modifier of a modifier is a menu
/// that needs rethinking rather than a data structure that needs recursion.
/// </param>
public sealed record OrderLineInstruction(
    Guid ProductId,
    string Description,
    decimal Quantity,
    Money UnitPrice,
    decimal TaxRate,
    Money DiscountAmount,
    int Course,
    int? SeatNumber,
    string? Note,
    bool IsPriceOverridden = false,
    Guid? OverriddenBy = null,
    IReadOnlyList<OrderLineInstruction>? Modifiers = null);

/// <summary>
/// Opens orders and puts lines on them, each in one transaction.
/// </summary>
/// <remarks>
/// <b>Not a Core port, and for the reason <c>ShiftWriter</c> gives.</b> There is no rule here
/// Core needs to own: the pricing is elsewhere and pure, and what remains is a counter upsert, a
/// row lock and a batch insert. Declaring a port for it would be adopting the repository pattern
/// <c>DECISIONS.md</c> explicitly did not adopt. Public rather than internal-behind-an-interface
/// for the same reason: <c>Pos.Api</c> references <c>Pos.Data</c> and its endpoints already use
/// <c>AppDbContext</c> directly.
/// <para>
/// <b>Nothing in here is money.</b> An order is working state — see <see cref="Order"/> — so this
/// writer takes no locks on a shift, touches no stock and writes no idempotency record. All three
/// happen later, once, when a bill is settled through <c>ISaleWriter</c>.
/// </para>
/// </remarks>
public sealed class OrderWriter(
    AppDbContext db,
    ITenantContext tenant,
    ICurrentActor actor,
    TimeProvider timeProvider)
{
    /// <summary>
    /// Opens an order, assigning its number inside the same transaction that inserts it.
    /// </summary>
    /// <remarks>
    /// <b>The collision on a table already being served is left to the index and to the
    /// caller.</b> A <c>DbUpdateException</c> on
    /// <c>ux_customer_order_tenant_table_open</c> surfaces from here and the endpoint turns it
    /// into a <see cref="TableAlreadyOccupiedException"/>, exactly as <c>ShiftEndpoints</c> does
    /// for a second open drawer. A pre-check here could not remove the race — two staff seating
    /// one table in the same second both pass it — it could only make the case rare enough to
    /// reach production and never a test.
    /// </remarks>
    public async Task<Order> OpenAsync(
        OrderOpenRequest request,
        Action<Order>? onCommitting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var openedAt = timeProvider.GetUtcNow();

        var openedBy = actor.UserId
            ?? throw new InvalidOperationException(
                "An order needs somebody to have opened it, and no user is attached to this request.");

        Order? order = null;

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            order = new Order
            {
                OrderNumber = await NextOrderNumberAsync(openedAt, cancellationToken),
                Type = request.Type,
                Status = OrderStatus.Open,
                DiningTableId = request.DiningTableId,
                TabName = request.TabName,
                RegisterId = request.RegisterId,
                CoverCount = request.CoverCount,
                Note = request.Note,
                OpenedBy = openedBy,
                OpenedAt = openedAt,
            };

            db.Orders.Add(order);

            // Saved before the callback so the id is stamped and can go into a stored response.
            // No navigation properties means no fixup — see CatalogFixture.
            await db.SaveChangesAsync(cancellationToken);

            onCommitting?.Invoke(order);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return order!;
    }

    /// <summary>
    /// Adds lines to an open order, numbering them under a lock on the order row.
    /// </summary>
    /// <returns>The inserted lines, parents before their own modifiers.</returns>
    /// <exception cref="OrderNotOpenException">It has been settled or abandoned.</exception>
    /// <remarks>
    /// <b>The lock is what makes line numbers mean anything.</b> Two waiters keying into one
    /// table at once is ordinary, not exotic — and without it both read the same
    /// <c>MAX(line_number)</c> and both insert line 7. The kitchen then gets two tickets that
    /// both say "7", and voiding "line 7" picks between them arbitrarily.
    /// <para>
    /// It is <c>FOR UPDATE</c> on one order's row, so it serialises adds to <i>that</i> order
    /// and nothing else. A busy room is many rows.
    /// </para>
    /// <para>
    /// The status is read under the same lock, so an order settled a moment earlier refuses this
    /// rather than racing it — the same pairing <c>SaleWriter</c> and <c>ShiftWriter</c> use to
    /// make a sale against a closing drawer deterministic.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<OrderLine>> AddLinesAsync(
        Guid orderId,
        IReadOnlyList<OrderLineInstruction> instructions,
        Action<IReadOnlyList<OrderLine>>? onCommitting,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(instructions);

        List<OrderLine> added = [];

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            added.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            await LockOpenOrderAsync(orderId, cancellationToken);

            var nextNumber = await NextLineNumberAsync(orderId, cancellationToken);

            foreach (var instruction in instructions)
            {
                var parent = Materialise(orderId, instruction, nextNumber++);

                db.OrderLines.Add(parent);
                added.Add(parent);

                // Saved before the children so the parent's id exists to point at. There are no
                // navigation properties in this model, so EF does no foreign-key fixup and a
                // child added alongside its parent would carry Guid.Empty.
                await db.SaveChangesAsync(cancellationToken);

                foreach (var modifier in instruction.Modifiers ?? [])
                {
                    var child = Materialise(orderId, modifier, nextNumber++);
                    child.ParentOrderLineId = parent.Id;

                    db.OrderLines.Add(child);
                    added.Add(child);
                }

                await db.SaveChangesAsync(cancellationToken);
            }

            onCommitting?.Invoke(added);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return added;
    }

    /// <summary>
    /// Takes an exclusive lock on one order and refuses if it is not open.
    /// </summary>
    /// <remarks>
    /// <b>Public because the bill path needs the same lock.</b> Allocating lines to a bill reads
    /// what is already allocated and then inserts, which is check-then-act: two waiters splitting
    /// one table in the same second both see a line as free and both take it. Serialising on the
    /// order row is the same answer used here for line numbers and in <c>KitchenTicketWriter</c>
    /// for firing, and one implementation is what stops the three drifting apart.
    /// <para>
    /// The caller must already be in a transaction — a lock outside one is released immediately
    /// and buys nothing.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Raw SQL because the lock is the point, and EF has no <c>FOR UPDATE</c>. The
    /// <c>tenant_id</c> predicate is written by hand for the reason <c>ShiftWriter</c> states:
    /// the global query filter composes over LINQ and not over this. That is not a bypass of
    /// invariant 2 — the tenant comes from the validated token exactly as everywhere else, and
    /// row-level security is underneath as the layer that holds when application code is wrong.
    /// </remarks>
    public async Task LockOpenOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var statuses = await db.Database
            .SqlQuery<string>(
                $"""
                 SELECT status AS "Value" FROM customer_order
                 WHERE tenant_id = {tenant.TenantId} AND id = {orderId}
                 FOR UPDATE
                 """)
            .ToListAsync(cancellationToken);

        if (statuses.Count == 0)
        {
            // The endpoint answers 404 for an unknown order before reaching the writer, so this
            // is the narrow race where it was abandoned between the two. Refused rather than
            // ignored: silently dropping a waiter's round is worse than telling them.
            throw new OrderNotOpenException("That order no longer exists.");
        }

        if (!string.Equals(statuses[0], nameof(OrderStatus.Open), StringComparison.Ordinal))
        {
            throw new OrderNotOpenException();
        }
    }

    /// <summary>The next line number for this order. Read under the caller's lock.</summary>
    private async Task<int> NextLineNumberAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var highest = await db.Database
            .SqlQuery<int>(
                $"""
                 SELECT COALESCE(MAX(line_number), 0) AS "Value" FROM customer_order_line
                 WHERE tenant_id = {tenant.TenantId} AND order_id = {orderId}
                 """)
            .FirstAsync(cancellationToken);

        // MAX rather than a count, so a voided line does not hand its number to the next item.
        // Numbers stay stable for the life of the order, which is what makes "void line 7" mean
        // one thing to the person saying it and to the kitchen hearing it.
        return highest + 1;
    }

    private static OrderLine Materialise(Guid orderId, OrderLineInstruction instruction, int lineNumber) =>
        new()
        {
            OrderId = orderId,
            ProductId = instruction.ProductId,
            LineNumber = lineNumber,
            Description = instruction.Description,
            Quantity = instruction.Quantity,
            UnitPrice = instruction.UnitPrice,
            TaxRate = instruction.TaxRate,
            DiscountAmount = instruction.DiscountAmount,
            Course = instruction.Course,
            SeatNumber = instruction.SeatNumber,
            Note = instruction.Note,
            IsPriceOverridden = instruction.IsPriceOverridden,
            OverriddenBy = instruction.OverriddenBy,
            Status = OrderLineStatus.Pending,
        };

    /// <summary>
    /// Takes the next order number for this tenant, inside the caller's transaction.
    /// </summary>
    /// <remarks>
    /// The same statement and the same reasoning as <c>SaleWriter.NextSaleNumberAsync</c>: one
    /// upsert, whose row-level exclusive lock makes a concurrent order in the same tenant block
    /// and then read the committed value. Not a Postgres sequence, because a sequence advances
    /// even when the transaction that drew from it rolls back.
    /// <para>
    /// Issued through ADO rather than EF because it is a data-modifying statement with a
    /// <c>RETURNING</c> clause, and EF's <c>SqlQuery</c> composes its argument into a subquery
    /// where Postgres does not allow one.
    /// </para>
    /// </remarks>
    private async Task<long> NextOrderNumberAsync(
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();

        await using var command = connection.CreateCommand();

        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        command.CommandText =
            """
            INSERT INTO customer_order_sequence (id, tenant_id, last_number, created_at)
            VALUES (@id, @tenant, 1, @now)
            ON CONFLICT (tenant_id) DO UPDATE
              SET last_number = customer_order_sequence.last_number + 1
            RETURNING last_number
            """;

        AddParameter(command, "id", Guid.CreateVersion7());
        AddParameter(command, "tenant", tenant.TenantId);
        AddParameter(command, "now", now);

        // The INSERT arm is the first order of a shop that has just turned restaurant mode on.
        // An upsert removes the question of who creates the row.
        return (long)(await command.ExecuteScalarAsync(cancellationToken))!;
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
