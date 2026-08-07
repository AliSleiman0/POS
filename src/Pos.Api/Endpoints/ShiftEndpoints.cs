using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Errors;
using Pos.Api.Idempotency;
using Pos.Core.Auditing;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Shifts;
using Pos.Data;
using Pos.Data.Reporting;
using Pos.Data.Shifts;

namespace Pos.Api.Endpoints;

/// <summary>Opening a register's drawer for trading.</summary>
public sealed record OpenShiftRequest(Guid? RegisterId, decimal? OpeningFloat);

/// <summary>The physical count at the end of a shift.</summary>
public sealed record CloseShiftRequest(decimal? CountedCash);

/// <summary>Cash into or out of the drawer other than through a sale.</summary>
/// <remarks>
/// <c>amount</c> is <b>signed</b> and its sign is checked against <c>type</c>: drops, payouts
/// and petty cash all take money out, so they are negative.
/// </remarks>
public sealed record CreateCashMovementRequest(string? Type, decimal? Amount, string? Reason);

/// <summary>A recorded cash movement.</summary>
public sealed record CashMovementResponse(
    Guid Id,
    Guid ShiftId,
    CashMovementType Type,
    decimal Amount,
    string Reason,
    Guid PerformedBy,
    DateTimeOffset OccurredAt);

/// <summary>A shift as the register sees it.</summary>
public sealed record ShiftResponse(
    Guid Id,
    Guid RegisterId,
    ShiftStatus Status,
    Guid OpenedBy,
    DateTimeOffset OpenedAt,
    decimal OpeningFloat,
    Guid? ClosedBy,
    DateTimeOffset? ClosedAt,
    decimal? CountedCash,
    decimal? ExpectedCash,
    decimal? Variance);

public static class ShiftEndpoints
{
    private const string OpenShiftConstraint = "ux_shift_tenant_register_open";

    public static IEndpointRouteBuilder MapShiftEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var shifts = builder.MapGroup("/api/v1/shifts").WithTags("Shifts");

        // CanSell, because opening the drawer is the first thing a cashier does at the start
        // of a shift and there may be nobody else in the shop to do it.
        shifts.MapPost("/", OpenAsync)
            .RequireAuthorization(Policies.CanSell)
            .RequireIdempotency()
            .WithSummary("Open a shift on a register");

        shifts.MapGet("/current", CurrentAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("The open shift for a register, or 404");

        // CanCloseShift, not CanSell: closing is the reconciliation step, and the variance it
        // produces is the number an owner reads. A cashier who could close their own drawer
        // could also decide what it was supposed to contain.
        shifts.MapPost("/{id:guid}/close", CloseAsync)
            .RequireAuthorization(Policies.CanCloseShift)
            .RequireIdempotency()
            .WithSummary("Close a shift against a counted drawer");

        // CanSell: a drop to the safe mid-shift is something the person on the till does, and
        // often the only person in the shop.
        shifts.MapPost("/{id:guid}/cash-movements", CashMovementAsync)
            .RequireAuthorization(Policies.CanSell)
            .RequireIdempotency()
            .WithSummary("Record cash into or out of the drawer");

        // CanCloseShift, matching the close itself: the Z-report is the reconciliation, and
        // whoever may read what the drawer should contain is whoever may reconcile it.
        shifts.MapGet("/{id:guid}/report", ReportAsync)
            .RequireAuthorization(Policies.CanCloseShift)
            .WithSummary("The Z-report for one shift");

        return builder;
    }

    private static async Task<Results<Created<ShiftResponse>, ValidationProblem>> OpenAsync(
        OpenShiftRequest request,
        AppDbContext db,
        IIdempotencyContext idempotency,
        ICurrentActor actor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>();

        if (request.RegisterId is not { } registerId || registerId == Guid.Empty)
        {
            errors["registerId"] = ["A register is required."];
        }
        else if (!await db.Registers.AnyAsync(r => r.Id == registerId && r.IsActive, cancellationToken))
        {
            // 400 on the field rather than 404, and identical whether the register is unknown,
            // inactive or another tenant's — so it is not an existence oracle.
            errors["registerId"] = ["No active register with that id exists in this tenant."];
        }

        if (request.OpeningFloat is not { } openingFloat)
        {
            errors["openingFloat"] = ["An opening float is required."];
        }
        else if (!CatalogRules.IsStorableAmount(openingFloat))
        {
            errors["openingFloat"] = ["A float of 0 or more with at most 4 decimal places is required."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var openedAt = timeProvider.GetUtcNow();

        var shift = new Shift
        {
            RegisterId = request.RegisterId!.Value,

            // From the validated token. A caller who could name the opener could attribute a
            // drawer — and its variance — to somebody who never touched it.
            OpenedBy = actor.UserId
                ?? throw new InvalidOperationException("No user is attached to this request."),
            OpenedAt = openedAt,
            OpeningFloat = (Money)request.OpeningFloat!.Value,
            Status = ShiftStatus.Open,
        };

        ShiftResponse? response = null;

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            db.Shifts.Add(shift);

            // Saved before the idempotency record so the shift's id is stamped and can go into
            // the stored response. No navigation properties means no fixup — see CatalogFixture.
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (PostgresErrors.IsUniqueViolation(exception, OpenShiftConstraint))
            {
                // The filtered unique index is the authority, not a pre-check: two tills
                // opening at the same moment both pass "is one already open?" and both insert,
                // which would leave a register with two drawers and no way to say which one a
                // sale belonged to.
                throw new ShiftAlreadyOpenException();
            }

            response = Project(shift);
            idempotency.Record(db, StatusCodes.Status201Created, response, openedAt);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return TypedResults.Created($"/api/v1/shifts/{shift.Id}", response!);
    }

    /// <summary>
    /// The open shift for a register, or 404.
    /// </summary>
    /// <remarks>
    /// A 404 rather than an empty 200: "there is no open shift" is the answer a register acts
    /// on by showing the open-shift screen, and a null body it has to unwrap first is a
    /// distinction without a difference that every client would get slightly wrong.
    /// </remarks>
    private static async Task<Results<Ok<ShiftResponse>, NotFound, ValidationProblem>> CurrentAsync(
        Guid? registerId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        if (registerId is not { } register || register == Guid.Empty)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["registerId"] = ["A register is required."],
            });
        }

        var shift = await db.Shifts
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.RegisterId == register && s.Status == ShiftStatus.Open,
                cancellationToken);

        return shift is null ? TypedResults.NotFound() : TypedResults.Ok(Project(shift));
    }

    private static async Task<Results<Ok<ShiftResponse>, NotFound, ValidationProblem>> CloseAsync(
        Guid id,
        CloseShiftRequest request,
        AppDbContext db,
        ShiftWriter writer,
        IIdempotencyContext idempotency,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CountedCash is not { } counted)
        {
            // Required, not defaulted. An omitted decimal binds to zero, and a drawer "counted"
            // as empty produces a variance equal to everything in it — which reads as theft.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["countedCash"] = ["A counted amount is required."],
            });
        }

        if (!CatalogRules.IsStorableAmount(counted))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["countedCash"] = ["An amount of 0 or more with at most 4 decimal places is required."],
            });
        }

        // 404 before the writer, so another tenant's shift is indistinguishable from one that
        // does not exist. The writer's locking UPDATE is what makes the decision safe.
        if (!await db.Shifts.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        await writer.CloseAsync(
            id,
            (Money)counted,
            closed => idempotency.Record(
                db,
                StatusCodes.Status200OK,
                new
                {
                    id = closed.ShiftId,
                    countedCash = (decimal)closed.CountedCash,
                    expectedCash = (decimal)closed.ExpectedCash,
                    variance = (decimal)closed.Variance,
                },
                closed.ClosedAt),
            cancellationToken);

        var shift = await db.Shifts.AsNoTracking().FirstAsync(s => s.Id == id, cancellationToken);

        return TypedResults.Ok(Project(shift));
    }

    private static async Task<Results<Created<CashMovementResponse>, NotFound, ValidationProblem>>
        CashMovementAsync(
            Guid id,
            CreateCashMovementRequest request,
            AppDbContext db,
            IIdempotencyContext idempotency,
            ICurrentActor actor,
            TimeProvider timeProvider,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>();

        CashMovementType? type = null;

        if (!Enum.TryParse<CashMovementType>(request.Type, ignoreCase: false, out var parsed)
            || !Enum.IsDefined(parsed))
        {
            errors["type"] = [$"One of {string.Join(", ", CashRules.AllowedTypes)} is required."];
        }
        else
        {
            type = parsed;
        }

        if (request.Amount is not { } amount)
        {
            errors["amount"] = ["An amount is required."];
        }
        else if (!CatalogRules.IsStorableSignedAmount(amount))
        {
            errors["amount"] = ["An amount with at most 4 decimal places is required."];
        }
        else if (type is { } known && !CashRules.IsSignConsistent(known, amount))
        {
            // The rule that catches a typed minus sign, or its absence. A drop entered as a
            // positive leaves the drawer wrong by twice the amount, in the direction nobody
            // notices until close.
            errors["amount"] = [CashRules.SignMessage(known)];
        }

        var reason = request.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            // Required, for the same reason a stock adjustment's is: this is the record you
            // need six months later and will not have.
            errors["reason"] = ["A reason is required."];
        }
        else if (reason.Length > CashMovement.ReasonMaxLength)
        {
            errors["reason"] = [$"A reason of at most {CashMovement.ReasonMaxLength} characters is required."];
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var shift = await db.Shifts
            .AsNoTracking()
            .Where(s => s.Id == id)
            .Select(s => new { s.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (shift is null)
        {
            return TypedResults.NotFound();
        }

        if (shift.Status != ShiftStatus.Open)
        {
            // A closed shift's expected cash is already computed and stored. Accepting a
            // movement against it would leave a variance that no longer explains the drawer.
            throw new ShiftClosedException(
                "That shift is closed, so its cash can no longer change.");
        }

        var occurredAt = timeProvider.GetUtcNow();

        var movement = new CashMovement
        {
            ShiftId = id,
            Type = type!.Value,
            Amount = (Money)request.Amount!.Value,
            Reason = reason!,

            // Server-set from the validated token, like every other actor in this system.
            PerformedBy = actor.UserId
                ?? throw new InvalidOperationException("No user is attached to this request."),
            OccurredAt = occurredAt,
        };

        CashMovementResponse? response = null;

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            db.CashMovements.Add(movement);
            await db.SaveChangesAsync(cancellationToken);

            response = new CashMovementResponse(
                movement.Id,
                movement.ShiftId,
                movement.Type,
                (decimal)movement.Amount,
                movement.Reason,
                movement.PerformedBy,
                movement.OccurredAt);

            idempotency.Record(db, StatusCodes.Status201Created, response, occurredAt);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return TypedResults.Created($"/api/v1/shifts/{id}", response!);
    }

    /// <summary>
    /// The Z-report for one shift.
    /// </summary>
    /// <remarks>
    /// <b>A closed shift's reconciliation is read, not recomputed.</b> <c>Shift.ExpectedCash</c>
    /// says why: recomputing it later would silently change a historical variance whenever
    /// anything about the underlying sales changed, which is the same class of mistake as
    /// joining a report to the current product price. A shift still open has no stored figure,
    /// so one is computed live and the response says <c>isProvisional</c> — two numbers with two
    /// meanings, never collapsed into one.
    /// <para>
    /// It shares every query with <c>GET /reports/daily</c> through one <see cref="ReportScope"/>.
    /// A shift's report and the day's report that contains it have to add up to the same money,
    /// and two sets of hand-written predicates is how that stops being true.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<ReportResponse>, NotFound>> ReportAsync(
        Guid id,
        AppDbContext db,
        ReportQueries reports,
        CancellationToken cancellationToken)
    {
        // Scoped by the query filter, so another tenant's shift is a 404 indistinguishable from
        // one that does not exist.
        if (!await db.Shifts.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var shop = await ReportEndpoints.ShopAsync(db, cancellationToken);

        var scope = ReportScope.ForShift(id);
        var data = await reports.ReadAsync(scope, cancellationToken);

        // The shift's own window, for the header — a Z-report is read next to the drawer it
        // reconciles, and "which shift is this?" is the first question.
        var shift = data.Shifts.Count == 0 ? null : data.Shifts[0];

        return TypedResults.Ok(ReportResponse.From(
            new ReportScopeResponse(
                "Shift",
                id,
                null,
                shift?.OpenedAt ?? DateTimeOffset.MinValue,
                shift?.ClosedAt ?? DateTimeOffset.MaxValue,
                shop.TimeZoneId),
            shop.CurrencyCode,
            data,
            LiveExpectedCash(data)));
    }

    /// <summary>Expected cash for a drawer nobody has counted yet. See the daily report's.</summary>
    private static Money LiveExpectedCash(ReportData data)
    {
        var open = data.Shifts.Where(s => s.Status == nameof(ShiftStatus.Open)).ToArray();

        if (open.Length == 0)
        {
            return Money.Zero;
        }

        var cash = data.Tenders.FirstOrDefault(t => t.Method == nameof(TenderMethod.Cash));

        return ShiftArithmetic.ExpectedCash(
            (Money)open.Sum(s => s.OpeningFloat),
            (Money)(cash is null ? 0m : cash.Amount - cash.ChangeGiven),
            (Money)data.CashMovements.Sum(m => m.Amount));
    }

    internal static ShiftResponse Project(Shift shift) => new(
        shift.Id,
        shift.RegisterId,
        shift.Status,
        shift.OpenedBy,
        shift.OpenedAt,
        (decimal)shift.OpeningFloat,
        shift.ClosedBy,
        shift.ClosedAt,
        (decimal?)shift.CountedCash,
        (decimal?)shift.ExpectedCash,
        (decimal?)shift.Variance);
}
