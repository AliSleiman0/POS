using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Idempotency;
using Pos.Api.Orders;
using Pos.Core.Auditing;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Pricing;
using Pos.Core.Sales;
using Pos.Core.Tenders;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>How much of one order line a bill takes.</summary>
public sealed record BillAllocationRequest(Guid? OrderLineId, decimal? Quantity);

/// <summary>The lines to put on a new bill.</summary>
/// <remarks>
/// Omitting <c>allocations</c> takes <b>everything still unbilled</b>, which is the ordinary
/// case: one table, one bill. Splitting is the exception and it is the one that needs spelling
/// out.
/// </remarks>
public sealed record CreateBillRequest(IReadOnlyList<BillAllocationRequest>? Allocations);

/// <summary>What is being handed over, and which drawer it goes into.</summary>
/// <remarks>
/// The register and shift travel in the body, exactly as they do on <c>POST /sales</c> and
/// <c>POST /sales/{id}/refund</c> — and they are the <b>current</b> ones, not the ones the order
/// was opened at. A table started on the terrace handheld and settled at the bar belongs to the
/// bar's drawer, because that is where the cash physically is.
/// </remarks>
public sealed record PayBillRequest(
    Guid? RegisterId,
    Guid? ShiftId,
    IReadOnlyList<BillTenderRequest>? Tenders,
    decimal? Tip);

/// <summary>One payment against a bill. Cash only, per <c>DECISIONS.md</c>.</summary>
public sealed record BillTenderRequest(decimal? Amount);

/// <summary>One priced line on a bill.</summary>
public sealed record BillLineResponse(
    Guid OrderLineId,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal LineTotal);

/// <summary>A bill, priced by the same engine that will charge it.</summary>
/// <remarks>
/// The amounts here come from <c>PricingEngine</c> over the bill's allocated lines — the same
/// call the payment makes. They are a <b>quote</b> until the bill is paid: nothing is stored,
/// and re-reading after a line is voided returns different numbers, correctly.
/// </remarks>
public sealed record OrderBillResponse(
    Guid Id,
    int BillNumber,
    OrderBillStatus Status,
    Guid ClientTransactionId,
    Guid? SaleId,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal RoundingAdjustment,
    decimal Total,
    decimal TipAmount,
    IReadOnlyList<BillLineResponse> Lines);

/// <summary>
/// Bills: the point where an order stops being working state and becomes money.
/// </summary>
/// <remarks>
/// <b>Paying a bill commits an ordinary <see cref="Sale"/> through <c>ISaleWriter</c></b> — the
/// same pricing engine, the same idempotency contract, the same stock ledger and the same
/// Z-report the retail till uses. There is no second money path, which is the whole reason the
/// order model was allowed to be a different shape in the first place.
/// <para>
/// <b>An even split is not here.</b> Four people paying a quarter each is four cash tenders
/// against one bill, which <c>Tender</c> has supported since Phase 3 — see
/// <see cref="OrderBill"/> for why allocating fractions to four bills would be wrong rather than
/// merely more work.
/// </para>
/// </remarks>
public static class OrderBillEndpoints
{
    public static IEndpointRouteBuilder MapOrderBillEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var bills = builder.MapGroup("/api/v1/orders/{id:guid}/bills")
            .WithTags("Orders")
            .RequireRestaurantMode();

        bills.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("This order's bills, priced");

        bills.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .RequireIdempotency()
            .WithSummary("Split off a bill");

        bills.MapDelete("/{billId:guid}", VoidAsync)
            .RequireAuthorization(Policies.CanTakeOrders)
            .WithSummary("Tear up a bill nobody has paid");

        bills.MapPost("/{billId:guid}/pay", PayAsync)
            .RequireAuthorization(Policies.CanSell)
            .RequireIdempotency()
            .WithSummary("Take the money for a bill");

        return builder;
    }

    private static async Task<Results<Ok<IReadOnlyList<OrderBillResponse>>, NotFound>> ListAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (!await db.Orders.AnyAsync(o => o.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await PriceAllAsync(db, id, cancellationToken));
    }

    private static async Task<Results<Created<OrderBillResponse>, NotFound, ValidationProblem>> CreateAsync(
        Guid id,
        CreateBillRequest request,
        AppDbContext db,
        IIdempotencyContext idempotency,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order is null)
        {
            return TypedResults.NotFound();
        }

        if (order.Status != OrderStatus.Open)
        {
            throw new OrderNotOpenException();
        }

        var lines = await ActiveLinesAsync(db, id, cancellationToken);
        var allocated = await AllocatedAsync(db, id, cancellationToken);

        var (wanted, errors) = ResolveAllocations(request.Allocations, lines, allocated);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (wanted.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["allocations"] = ["There is nothing left to bill on this order."],
            });
        }

        var nextNumber = await db.OrderBills
            .Where(b => b.OrderId == id)
            .Select(b => (int?)b.BillNumber)
            .MaxAsync(cancellationToken) ?? 0;

        var bill = new OrderBill
        {
            OrderId = id,
            BillNumber = nextNumber + 1,
            Status = OrderBillStatus.Open,

            // Minted with the bill, not with the payment attempt. Invariant 6: a key generated
            // per attempt makes the header decorative and charges the table twice on a retry.
            ClientTransactionId = Guid.CreateVersion7(),
        };

        db.OrderBills.Add(bill);
        await db.SaveChangesAsync(cancellationToken);

        foreach (var (lineId, quantity) in wanted)
        {
            db.OrderBillLines.Add(new OrderBillLine
            {
                OrderBillId = bill.Id,
                OrderLineId = lineId,
                Quantity = quantity,
            });
        }

        idempotency.Record(db, StatusCodes.Status201Created, new { billId = bill.Id }, timeProvider.GetUtcNow());

        await db.SaveChangesAsync(cancellationToken);

        var priced = await PriceAllAsync(db, id, cancellationToken);

        return TypedResults.Created(
            $"/api/v1/orders/{id}/bills/{bill.Id}",
            priced.First(b => b.Id == bill.Id));
    }

    private static async Task<Results<NoContent, NotFound>> VoidAsync(
        Guid id,
        Guid billId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var bill = await db.OrderBills
            .FirstOrDefaultAsync(b => b.Id == billId && b.OrderId == id, cancellationToken);

        if (bill is null)
        {
            return TypedResults.NotFound();
        }

        if (bill.Status == OrderBillStatus.Paid)
        {
            // A paid bill is a completed sale, and invariant 4 says those are never undone by
            // deletion. Reversing one is a refund against the sale, which Phase 3.7 owns.
            throw new OrderNotOpenException(
                "That bill has been paid. Refund the sale it became rather than tearing it up.");
        }

        // The allocations go, so the lines return to unbilled and can be split again. The bill
        // row stays as a Voided marker: bill numbers are what a waiter says out loud, and
        // reusing "bill two" for something else within one order is how a table gets confused.
        var allocations = await db.OrderBillLines
            .Where(l => l.OrderBillId == billId)
            .ToListAsync(cancellationToken);

        db.OrderBillLines.RemoveRange(allocations);

        bill.Status = OrderBillStatus.Voided;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Takes the money for a bill and commits it as an ordinary sale.
    /// </summary>
    /// <remarks>
    /// <b>Everything below this line is the retail path.</b> The lines are priced by
    /// <c>PricingEngine</c>, committed by <c>ISaleWriter</c> with the bill's own
    /// <c>ClientTransactionId</c>, and the stock ledger and the shift lock do what they always
    /// do. That is the design's central claim and this is where it is either true or not.
    /// </remarks>
    private static async Task<Results<Ok<OrderBillResponse>, NotFound, ValidationProblem>> PayAsync(
        Guid id,
        Guid billId,
        PayBillRequest request,
        AppDbContext db,
        ISaleWriter writer,
        IIdempotencyContext idempotency,
        ICurrentActor actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timeProvider);

        var order = await db.Orders.FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        var bill = await db.OrderBills
            .FirstOrDefaultAsync(b => b.Id == billId && b.OrderId == id, cancellationToken);

        if (order is null || bill is null)
        {
            return TypedResults.NotFound();
        }

        if (bill.Status != OrderBillStatus.Open)
        {
            // Not an error to retry. A paid bill's own sale is the answer, and re-paying would
            // charge the table twice — which the idempotency key already prevents, so reaching
            // here means a genuinely second attempt with a second key.
            throw new OrderNotOpenException("That bill has already been settled.");
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        var tip = request.Tip ?? 0m;

        if (tip < 0m || !CatalogRules.IsStorableAmount(tip))
        {
            errors["tip"] = ["A tip of 0 or more with at most 4 decimal places is required."];
        }

        if (request.Tenders is not { Count: > 0 })
        {
            errors["tenders"] = ["At least one tender is required."];
        }

        if (request.RegisterId is not { } registerId || registerId == Guid.Empty)
        {
            errors["registerId"] = ["A register is required."];
        }
        else if (!await db.Registers.AnyAsync(r => r.Id == registerId && r.IsActive, cancellationToken))
        {
            // 400 on the field rather than 404, identical whether the register is unknown,
            // inactive or another tenant's — so it is not an existence oracle. The same answer
            // POST /shifts gives.
            errors["registerId"] = ["No active register with that id exists in this shop."];
        }

        if (request.ShiftId is not { } shiftId || shiftId == Guid.Empty)
        {
            errors["shiftId"] = ["A shift is required."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        // The writer re-checks this under its row lock, which is what makes a concurrent close
        // deterministic. Checked here too so the message names the problem rather than surfacing
        // as a bare conflict from three layers down.
        var shift = await db.Shifts
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == request.ShiftId, cancellationToken);

        if (shift is null || shift.Status != ShiftStatus.Open)
        {
            // Without an open drawer there is nothing to reconcile the cash against, and
            // "we're short" becomes unanswerable.
            throw new ShiftClosedException("That shift is not open.");
        }

        var cart = await BuildCartAsync(db, bill.Id, cancellationToken);

        if (cart.Lines.Count == 0)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["allocations"] = ["This bill has nothing on it."],
            });
        }

        var priced = PricingEngine.Price(cart);

        var tenders = request.Tenders!
            .Select(t => new TenderInstruction(TenderMethod.Cash, (Money)(t.Amount ?? 0m)))
            .ToList();

        if (!TenderRules.IsSufficient(priced.Total + (Money)tip, [.. tenders.Select(t => t.Amount)]))
        {
            throw new UnderTenderException(
                $"The bill comes to {priced.Total} with a tip of {tip}.");
        }

        var tracked = await TrackedProductIdsAsync(db, cart, cancellationToken);

        var committed = await writer.CommitAsync(
            new SaleCommitRequest(
                // The bill's key, minted when the bill was created and reused on every attempt.
                bill.ClientTransactionId,
                request.RegisterId!.Value,
                shift.Id,

                // Snapshotted onto the sale, so a report never re-reads the tenant — the same
                // reason a retail sale carries it.
                cart.TaxMode,
                priced,
                tenders,
                tracked,
                AuthorizedBy: null,
                OccurredAt: null,
                Tip: (Money)tip),
            result =>
            {
                bill.Status = OrderBillStatus.Paid;
                bill.SaleId = result.SaleId;
                bill.TipAmount = (Money)tip;
                bill.PaidAt = result.CompletedAt;
                bill.PaidBy = actor.UserId;

                idempotency.Record(
                    db,
                    StatusCodes.Status200OK,
                    new { billId = bill.Id, saleId = result.SaleId, saleNumber = result.SaleNumber },
                    result.RecordedAt);
            },
            cancellationToken);

        // Inside no transaction of its own: the writer saved the bill's own change above as
        // part of its commit, and this only decides whether the order is finished.
        await CloseIfSettledAsync(db, order, actor, timeProvider, cancellationToken);

        var priceds = await PriceAllAsync(db, id, cancellationToken);

        return TypedResults.Ok(priceds.First(b => b.Id == billId) with { SaleId = committed.SaleId });
    }

    /// <summary>
    /// Closes the order once every line is billed and every bill is paid.
    /// </summary>
    /// <remarks>
    /// <b>Both halves, and the first is the one that matters.</b> An order closed with lines
    /// nobody paid for is food given away that no report would ever show — so a table with an
    /// unallocated line stays open, visibly, until somebody deals with it.
    /// </remarks>
    private static async Task CloseIfSettledAsync(
        AppDbContext db,
        Order order,
        ICurrentActor actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var lines = await ActiveLinesAsync(db, order.Id, cancellationToken);
        var allocated = await AllocatedAsync(db, order.Id, cancellationToken);

        var everythingBilled = lines.All(line =>
            allocated.GetValueOrDefault(line.Id) >= line.Quantity);

        if (!everythingBilled)
        {
            return;
        }

        var unpaid = await db.OrderBills
            .AnyAsync(b => b.OrderId == order.Id && b.Status == OrderBillStatus.Open, cancellationToken);

        if (unpaid)
        {
            return;
        }

        order.Status = OrderStatus.Closed;
        order.ClosedAt = timeProvider.GetUtcNow();
        order.ClosedBy = actor.UserId;

        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Turns a bill's allocations into a cart the pricing engine can read.
    /// </summary>
    /// <remarks>
    /// <b>Built from the order line's own snapshots</b>, never from the catalog: the guest pays
    /// the price they were quoted when they ordered, which may be an hour and a menu change ago.
    /// The same rule <c>SaleWriter.RefundAsync</c> applies when it re-prices from a sale line.
    /// </remarks>
    private static async Task<Cart> BuildCartAsync(
        AppDbContext db,
        Guid billId,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from allocation in db.OrderBillLines.AsNoTracking()
            join line in db.OrderLines on allocation.OrderLineId equals line.Id
            where allocation.OrderBillId == billId
            orderby line.LineNumber
            select new { allocation.Quantity, Line = line }).ToListAsync(cancellationToken);

        var shop = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == db.CurrentTenantId)
            .Select(t => new { t.TaxMode, t.CashRoundingIncrement })
            .FirstAsync(cancellationToken);

        var lines = rows.Select(row => new CartLine(
            row.Line.ProductId,
            row.Line.Description,
            row.Quantity,
            row.Line.UnitPrice,
            row.Line.TaxRate,

            // The line's discount, scaled to the share this bill takes. A half-bottle on one of
            // two bills carries half the discount, so the parts still sum to the whole.
            ShareOf(row.Line.DiscountAmount, row.Quantity, row.Line.Quantity),
            row.Line.IsPriceOverridden)).ToList();

        return new Cart(lines, Money.Zero, shop.TaxMode, shop.CashRoundingIncrement);
    }

    /// <summary>A discount's proportional share, matching <c>RefundRules.DiscountShare</c>.</summary>
    private static Money ShareOf(Money discount, decimal taken, decimal whole)
    {
        if (discount.IsZero || whole == 0m)
        {
            return Money.Zero;
        }

        return taken == whole ? discount : (discount * (taken / whole)).RoundToStorage();
    }

    private static async Task<IReadOnlySet<Guid>> TrackedProductIdsAsync(
        AppDbContext db,
        Cart cart,
        CancellationToken cancellationToken)
    {
        var ids = cart.Lines.Select(l => l.ProductId).Distinct().ToArray();

        var tracked = await db.Products
            .AsNoTracking()
            .Where(p => ids.Contains(p.Id) && p.TrackStock)
            .Select(p => p.Id)
            .ToListAsync(cancellationToken);

        return tracked.ToHashSet();
    }

    /// <summary>The order's lines that are still on it — voided ones are not billed.</summary>
    private static async Task<List<OrderLine>> ActiveLinesAsync(
        AppDbContext db,
        Guid orderId,
        CancellationToken cancellationToken) =>
        await db.OrderLines
            .AsNoTracking()
            .Where(l => l.OrderId == orderId && l.Status != OrderLineStatus.Voided)
            .OrderBy(l => l.LineNumber)
            .ToListAsync(cancellationToken);

    /// <summary>How much of each line is already on a bill that has not been torn up.</summary>
    private static async Task<Dictionary<Guid, decimal>> AllocatedAsync(
        AppDbContext db,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var rows = await (
            from allocation in db.OrderBillLines.AsNoTracking()
            join bill in db.OrderBills on allocation.OrderBillId equals bill.Id
            where bill.OrderId == orderId && bill.Status != OrderBillStatus.Voided
            select new { allocation.OrderLineId, allocation.Quantity }).ToListAsync(cancellationToken);

        return rows
            .GroupBy(row => row.OrderLineId)
            .ToDictionary(group => group.Key, group => group.Sum(row => row.Quantity));
    }

    /// <summary>
    /// Works out what a new bill takes, defaulting to everything still unbilled.
    /// </summary>
    /// <remarks>
    /// <b>Over-allocation is refused rather than clamped.</b> Billing three of two is somebody
    /// having split the table wrong, and silently trimming it would charge for two while the
    /// screen said three — a discrepancy nobody would find until the drawer was counted.
    /// </remarks>
    private static (List<(Guid LineId, decimal Quantity)> Wanted, Dictionary<string, string[]> Errors)
        ResolveAllocations(
            IReadOnlyList<BillAllocationRequest>? requested,
            IReadOnlyList<OrderLine> lines,
            IReadOnlyDictionary<Guid, decimal> allocated)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var wanted = new List<(Guid, decimal)>();

        if (requested is null)
        {
            // The ordinary case: one table, one bill, everything on it.
            foreach (var line in lines)
            {
                var remaining = line.Quantity - allocated.GetValueOrDefault(line.Id);

                if (remaining > 0m)
                {
                    wanted.Add((line.Id, remaining));
                }
            }

            return (wanted, errors);
        }

        for (var index = 0; index < requested.Count; index++)
        {
            var allocation = requested[index];

            var line = allocation.OrderLineId is { } lineId
                ? lines.FirstOrDefault(l => l.Id == lineId)
                : null;

            if (line is null)
            {
                errors[$"allocations[{index}].orderLineId"] =
                    ["That line is not on this order, or has been voided."];
                continue;
            }

            var quantity = allocation.Quantity ?? (line.Quantity - allocated.GetValueOrDefault(line.Id));

            if (quantity <= 0m || !CatalogRules.IsStorableAmount(quantity))
            {
                errors[$"allocations[{index}].quantity"] =
                    ["A quantity greater than zero with at most 4 decimal places is required."];
                continue;
            }

            var remaining = line.Quantity - allocated.GetValueOrDefault(line.Id);

            if (quantity > remaining)
            {
                errors[$"allocations[{index}].quantity"] =
                    [$"Only {remaining} of that line is still unbilled."];
                continue;
            }

            wanted.Add((line.Id, quantity));
        }

        return (wanted, errors);
    }

    /// <summary>Prices every bill on an order, through the engine that will charge them.</summary>
    private static async Task<IReadOnlyList<OrderBillResponse>> PriceAllAsync(
        AppDbContext db,
        Guid orderId,
        CancellationToken cancellationToken)
    {
        var bills = await db.OrderBills
            .AsNoTracking()
            .Where(b => b.OrderId == orderId)
            .OrderBy(b => b.BillNumber)
            .ToListAsync(cancellationToken);

        if (bills.Count == 0)
        {
            return [];
        }

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId, cancellationToken);

        var responses = new List<OrderBillResponse>();

        foreach (var bill in bills)
        {
            var cart = await BuildCartAsync(db, bill.Id, cancellationToken);

            if (cart.Lines.Count == 0)
            {
                responses.Add(new OrderBillResponse(
                    bill.Id, bill.BillNumber, bill.Status, bill.ClientTransactionId, bill.SaleId,
                    0m, 0m, 0m, 0m, 0m, (decimal)bill.TipAmount, []));

                continue;
            }

            var priced = PricingEngine.Price(cart);

            responses.Add(new OrderBillResponse(
                bill.Id,
                bill.BillNumber,
                bill.Status,
                bill.ClientTransactionId,
                bill.SaleId,
                (decimal)priced.Subtotal,
                (decimal)priced.DiscountTotal,
                (decimal)priced.TaxTotal,
                (decimal)priced.RoundingAdjustment,
                (decimal)priced.Total,
                (decimal)bill.TipAmount,
                [.. priced.Lines.Select((line, index) => new BillLineResponse(
                    cart.Lines[index].ProductId,
                    line.Source.Description,
                    line.Source.Quantity,
                    (decimal)line.Source.UnitPrice,
                    (decimal)line.Total))]));
        }

        return responses;
    }
}
