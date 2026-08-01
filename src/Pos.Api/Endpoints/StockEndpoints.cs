using System.Linq.Expressions;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Idempotency;
using Pos.Core.Entities;
using Pos.Core.Inventory;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>
/// A manual stock movement. Every field nullable for the reason
/// <see cref="CreateProductRequest"/> gives: an omitted <c>decimal</c> binds to zero, which
/// here would be a movement that moves nothing and reports success.
/// </summary>
public sealed record CreateStockAdjustmentRequest(
    Guid? ProductId,
    string? Type,
    decimal? Quantity,
    string? Reason);

/// <summary>One product's stock, as the on-hand list shows it.</summary>
/// <remarks>
/// <c>id</c> is the <b>product's</b> id, not the stock row's. The stock row is an
/// implementation detail — it may not exist yet — whereas every other endpoint a client will
/// call next takes a product id.
/// </remarks>
public sealed record StockLevelResponse(
    Guid Id,
    string Sku,
    string Name,
    Unit Unit,
    decimal OnHand,
    decimal? ReorderPoint,
    bool BelowReorderPoint,
    bool IsActive);

/// <summary>One line of the ledger.</summary>
public sealed record StockMovementResponse(
    Guid Id,
    Guid ProductId,
    StockMovementType Type,
    decimal Quantity,
    string? Reason,
    Guid? SaleId,
    Guid? PerformedBy,
    DateTimeOffset OccurredAt);

/// <summary>The movement that was written, and where it left the total.</summary>
public sealed record StockAdjustmentResponse(StockMovementResponse Movement, decimal OnHand);

public static class StockEndpoints
{
    private const string LevelSort = "stock:name";

    private const string LedgerSort = "stock-movement:occurred";

    public static IEndpointRouteBuilder MapStockEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var stock = builder.MapGroup("/api/v1/stock")
            .WithTags("Stock");

        // CanSell: a cashier being able to answer "have we got any more out the back?" is
        // the point of having the number at all.
        stock.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("On-hand levels");

        stock.MapGet("/{productId:guid}/movements", MovementsAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("The ledger for one product");

        stock.MapPost("/adjustments", AdjustAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .RequireIdempotency()
            .WithSummary("Receive, correct or write off stock");

        return builder;
    }

    /// <summary>
    /// Driven off <c>Products</c> rather than <c>StockItems</c>, deliberately.
    /// </summary>
    /// <remarks>
    /// The keyset needs a sort key with a total order, and the only humane one is the product
    /// name — an on-hand list ordered by an opaque product GUID is unusable by the person
    /// reading it. Driving off products also means a product that has never been counted
    /// shows a zero instead of vanishing, which is the difference between "we have none" and
    /// "this product is not in the report".
    /// </remarks>
    private static async Task<Results<Ok<CursorPage<StockLevelResponse>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        string? cursor,
        int? limit,
        bool? belowReorderPoint,
        bool? activeOnly,
        CancellationToken cancellationToken)
    {
        if (!PageQuery.TryRead<string>(cursor, limit, LevelSort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        // Products that do not track stock are not in this list at all. A carrier bag with an
        // on-hand of zero is noise in a report whose job is to show what needs ordering.
        var query = db.Products.AsNoTracking().Where(p => p.TrackStock);

        if (activeOnly ?? true)
        {
            query = query.Where(p => p.IsActive);
        }

        if (belowReorderPoint ?? false)
        {
            // Expressed against the stock row rather than the projection, so Postgres filters
            // rather than the server. A product with no stock row has no reorder point and so
            // cannot be below one.
            query = query.Where(p => db.StockItems.Any(s =>
                s.ProductId == p.Id
                && s.ReorderPoint != null
                && s.OnHand <= s.ReorderPoint));
        }

        return TypedResults.Ok(await query.ToPageAsync(p => p.Name, Project(db), page, cancellationToken));
    }

    private static async Task<Results<Ok<CursorPage<StockMovementResponse>>, NotFound, ValidationProblem>>
        MovementsAsync(
            Guid productId,
            AppDbContext db,
            string? cursor,
            int? limit,
            CancellationToken cancellationToken)
    {
        // Oldest first. The ledger reads as a story of what happened to this product, and
        // that is also the order ix_stock_movement_tenant_product_occurred already holds and
        // the order a rebuild replays. A newest-first view is a Phase 6 question, when there
        // is a screen with an opinion about it.
        if (!PageQuery.TryRead<DateTimeOffset>(cursor, limit, LedgerSort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!await db.Products.AnyAsync(p => p.Id == productId, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var results = await db.StockMovements
            .AsNoTracking()
            .Where(m => m.ProductId == productId)
            .ToPageAsync(
                m => m.OccurredAt,
                m => new StockMovementResponse(
                    m.Id, m.ProductId, m.Type, m.Quantity, m.Reason, m.SaleId, m.PerformedBy, m.OccurredAt),
                page,
                cancellationToken);

        return TypedResults.Ok(results);
    }

    /// <summary>
    /// Writes one movement through <see cref="IStockLedger"/>, exactly once.
    /// </summary>
    /// <remarks>
    /// <b>Idempotent since Phase 3.5</b>, using the same mechanism as sales, voids, refunds and
    /// shifts — which is why it was deferred rather than given a stock-shaped copy of its own
    /// in 2.4. A resubmitted adjustment now replays the original response instead of writing a
    /// second movement.
    /// <para>
    /// The transaction is owned here rather than by the ledger, because the idempotency record
    /// has to be inserted alongside the movement or not at all. That is what
    /// <c>RecordBatchAsync</c> exists for, and the single-element batch is not a workaround:
    /// it is the same code path the sale writer uses, exercised on the simplest possible case.
    /// </para>
    /// </remarks>
    private static async Task<Results<Created<StockAdjustmentResponse>, ValidationProblem>> AdjustAsync(
        CreateStockAdjustmentRequest request,
        AppDbContext db,
        IStockLedger ledger,
        IIdempotencyContext idempotency,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var fields = await ValidateAsync(db, request, cancellationToken);

        if (fields.Errors.Count > 0)
        {
            return TypedResults.ValidationProblem(fields.Errors);
        }

        // Read before the retry block, like StockLedger does: a transient-failure replay must
        // not re-timestamp the record, or the stored response would disagree with the movement
        // it describes about when it happened.
        var completedAt = timeProvider.GetUtcNow();

        StockAdjustmentResponse? response = null;

        // A retrying execution strategy refuses a user-initiated transaction outright — it
        // cannot replay a block it does not own — so the whole unit goes inside one.
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            var written = await ledger.RecordBatchAsync(
                [
                    new StockMovementRequest(
                        request.ProductId!.Value,
                        fields.Type!.Value,
                        request.Quantity!.Value,
                        fields.Reason),
                ],
                cancellationToken);

            var result = written[0];

            response = new StockAdjustmentResponse(
                new StockMovementResponse(
                    result.MovementId,
                    request.ProductId.Value,
                    fields.Type.Value,
                    request.Quantity.Value,
                    fields.Reason,
                    SaleId: null,
                    PerformedBy: null,
                    result.OccurredAt),
                result.OnHand);

            idempotency.Record(db, StatusCodes.Status201Created, response, completedAt);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        // The ledger for the product, since a single movement has no route of its own — the
        // useful thing to look at after adjusting stock is what the product's history now says.
        return TypedResults.Created(
            $"/api/v1/stock/{request.ProductId!.Value}/movements",
            response!);
    }

    private sealed record ValidatedFields(
        StockMovementType? Type,
        string? Reason,
        Dictionary<string, string[]> Errors);

    /// <summary>
    /// Checks every field before returning, so a request wrong in three places is told about
    /// all three.
    /// </summary>
    private static async Task<ValidatedFields> ValidateAsync(
        AppDbContext db,
        CreateStockAdjustmentRequest request,
        CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        StockMovementType? type = null;

        if (string.IsNullOrWhiteSpace(request.Type))
        {
            errors["type"] = [$"One of {AllowedTypes} is required."];
        }
        else if (!Enum.TryParse<StockMovementType>(request.Type, ignoreCase: false, out var parsed)
                 || !Enum.IsDefined(parsed))
        {
            errors["type"] = [$"One of {AllowedTypes} is required."];
        }
        else if (!StockRules.IsManualAdjustment(parsed))
        {
            // Named separately from "not a type at all", because the caller has done
            // something reasonable: Sale is a real movement type, it is just written by the
            // sale that caused it and carries that sale's id.
            errors["type"] = [$"{parsed} movements are not written by hand. Use one of {AllowedTypes}."];
        }
        else
        {
            type = parsed;
        }

        if (request.Quantity is not { } quantity)
        {
            errors["quantity"] = ["A quantity is required."];
        }
        else if (!StockRules.IsStorableQuantity(quantity))
        {
            errors["quantity"] = ["A quantity with at most 4 decimal places is required."];
        }
        else if (type is { } known && !StockRules.IsSignConsistent(known, quantity))
        {
            // The rule that catches a typed minus sign. A receipt of −5 is a stock figure
            // wrong by ten, in the direction nobody notices until stocktake.
            errors["quantity"] = [SignMessage(known)];
        }

        var reason = request.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            // Required, not optional. An adjustment with no reason is exactly the record you
            // need six months later and will not have.
            errors["reason"] = ["A reason is required."];
        }
        else if (reason.Length > StockMovement.ReasonMaxLength)
        {
            errors["reason"] = [$"A reason of at most {StockMovement.ReasonMaxLength} characters is required."];
        }

        if (request.ProductId is not { } productId || productId == Guid.Empty)
        {
            errors["productId"] = ["A product is required."];
        }
        else
        {
            var product = await db.Products
                .AsNoTracking()
                .Where(p => p.Id == productId)
                .Select(p => new { p.TrackStock })
                .FirstOrDefaultAsync(cancellationToken);

            if (product is null)
            {
                // 400 on the field rather than 404, and the same answer whether the id is
                // unknown or belongs to another shop — so it is not an existence oracle.
                errors["productId"] = ["No product with that id exists in this tenant."];
            }
            else if (!product.TrackStock)
            {
                // Otherwise a service or an open-price item accumulates an on-hand figure
                // nobody reads and no stocktake will ever reconcile.
                errors["productId"] = ["That product does not track stock."];
            }
        }

        return new ValidatedFields(type, reason, errors);
    }

    private static string AllowedTypes =>
        string.Join(", ", StockRules.ManualAdjustmentTypes);

    private static string SignMessage(StockMovementType type) => type switch
    {
        StockMovementType.Receive => "A receipt adds stock, so its quantity must be greater than zero.",
        StockMovementType.Waste => "A write-off removes stock, so its quantity must be less than zero.",
        _ => "A correction must move something, so its quantity cannot be zero.",
    };

    /// <summary>
    /// The stock figures read through a correlated subquery rather than a join.
    /// </summary>
    /// <remarks>
    /// A product with no stock row still appears, carrying zero. A join would drop it, and
    /// the products missing from the report would be exactly the ones nobody has counted —
    /// the ones most worth seeing.
    /// </remarks>
    private static Expression<Func<Product, StockLevelResponse>> Project(AppDbContext db) =>
        p => new StockLevelResponse(
            p.Id,
            p.Sku,
            p.Name,
            p.Unit,
            db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.OnHand).FirstOrDefault(),
            db.StockItems.Where(s => s.ProductId == p.Id).Select(s => s.ReorderPoint).FirstOrDefault(),
            db.StockItems.Any(s =>
                s.ProductId == p.Id && s.ReorderPoint != null && s.OnHand <= s.ReorderPoint),
            p.IsActive);
}
