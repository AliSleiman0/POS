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
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>Opening a register's drawer for trading.</summary>
public sealed record OpenShiftRequest(Guid? RegisterId, decimal? OpeningFloat);

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
