using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Errors;
using Pos.Core.Catalog;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Data;

namespace Pos.Api.Endpoints;

public sealed record CreateTaxClassRequest(string? Name, decimal? Rate, bool? IsDefault);

/// <summary>
/// A full replacement of the mutable fields. <c>isDefault</c> omitted leaves it unchanged
/// rather than clearing it, so an edit to the name cannot silently demote the tenant's
/// default.
/// </summary>
public sealed record UpdateTaxClassRequest(string? Name, decimal? Rate, bool? IsDefault);

public sealed record TaxClassResponse(
    Guid Id,
    string Name,
    decimal Rate,
    bool IsDefault,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt);

public static class TaxClassEndpoints
{
    /// <summary>
    /// Identifies this list's ordering inside a cursor, so one minted here cannot be
    /// replayed against a differently-ordered endpoint.
    /// </summary>
    private const string Sort = "tax-class:name";

    /// <summary>The unique index a concurrent "make this the default" loses on.</summary>
    private const string DefaultConstraint = "ux_tax_class_tenant_default";

    public static IEndpointRouteBuilder MapTaxClassEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // No group-level RequireAuthorization: reading a rate is CanSell because the
        // register needs it to price a line, while changing one is CanManageCatalog. Stated
        // per route, as in EmployeeEndpoints.
        var taxClasses = builder.MapGroup("/api/v1/tax-classes")
            .WithTags("Tax classes");

        taxClasses.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Every tax class in this tenant");

        taxClasses.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Create a tax class");

        taxClasses.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Policies.CanManageCatalog)
            .WithSummary("Replace a tax class");

        // No deactivate, and none invented: docs/API.md defines none and TaxClass has no
        // IsActive to set. The consequence is real -- a retired legislated rate stays in the
        // picker -- and is recorded in the handoff rather than solved by guessing here.

        return builder;
    }

    private static async Task<Results<Ok<CursorPage<TaxClassResponse>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        string? cursor,
        int? limit,
        CancellationToken cancellationToken)
    {
        if (!PageQuery.TryRead<string>(cursor, limit, Sort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var results = await db.TaxClasses
            .AsNoTracking()
            .ToPageAsync(t => t.Name, Project, page, cancellationToken);

        return TypedResults.Ok(results);
    }

    private static async Task<Results<Created<TaxClassResponse>, ValidationProblem>> CreateAsync(
        CreateTaxClassRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidate(request.Name, request.Rate, out var name, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        // No TenantId here, and none accepted from the body: the interceptor stamps it from
        // the validated token. Invariant 2.
        var taxClass = new TaxClass
        {
            Name = name,
            Rate = request.Rate!.Value,
        };

        db.TaxClasses.Add(taxClass);

        // A first tax class is not promoted to default automatically. Product writes always
        // name a tax class explicitly, so nothing reads the default in 2.2 -- and a rule
        // that only fires on the very first row is one nobody remembers a year later.
        await SaveWithDefaultAsync(db, taxClass, request.IsDefault ?? false, cancellationToken);

        return TypedResults.Created($"/api/v1/tax-classes/{taxClass.Id}", Map(taxClass));
    }

    private static async Task<Results<Ok<TaxClassResponse>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateTaxClassRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!TryValidate(request.Name, request.Rate, out var name, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var taxClass = await db.TaxClasses.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);

        if (taxClass is null)
        {
            // 404 rather than 403 for another tenant's row: the query filter has already
            // made it invisible, and a 403 would confirm the id exists somewhere.
            //
            // This lookup MUST come before the default is touched below. The other way
            // round, a cross-tenant PUT carrying isDefault would clear the *caller's* own
            // default and then answer 404 -- a 404 that lies, and one the manifest's
            // AssertUntouched exists to catch.
            return TypedResults.NotFound();
        }

        taxClass.Name = name;
        taxClass.Rate = request.Rate!.Value;

        // Omitted means unchanged. Binding an absent bool to false would demote the
        // tenant's default every time somebody corrected a spelling.
        await SaveWithDefaultAsync(db, taxClass, request.IsDefault ?? taxClass.IsDefault, cancellationToken);

        return TypedResults.Ok(Map(taxClass));
    }

    /// <summary>
    /// Saves <paramref name="taxClass"/>, making it the tenant's default if asked, and
    /// clearing whichever row held that first.
    /// </summary>
    /// <remarks>
    /// <c>ux_tax_class_tenant_default</c> is a filtered unique index and is not deferrable,
    /// so one <c>SaveChanges</c> that clears one row and sets another can violate it
    /// depending on the order EF emits the two updates. Clearing first, in its own
    /// statement, is what makes the order deterministic.
    /// <para>
    /// Clear-then-set rather than refusing a second default with a 409: "make this the
    /// default" is what the user means, and refusing would force a two-call dance with a
    /// window in which the shop has no default at all. It would also put a database
    /// constraint into the API contract.
    /// </para>
    /// <para>
    /// Tracked entities and two saves rather than one <c>ExecuteUpdateAsync</c>, because
    /// <c>ExecuteUpdate</c> bypasses <c>TenantSaveChangesInterceptor</c> and the demoted row
    /// would lose its <c>UpdatedAt</c>/<c>UpdatedBy</c> stamp. Tenancy is safe either way --
    /// query filters apply and RLS is at the connection -- but on a rare admin action the
    /// audit columns are worth the extra round trip.
    /// </para>
    /// </remarks>
    private static async Task SaveWithDefaultAsync(
        AppDbContext db,
        TaxClass taxClass,
        bool shouldBeDefault,
        CancellationToken cancellationToken)
    {
        // The connection is configured with EnableRetryOnFailure, and a retrying execution
        // strategy refuses a user-initiated transaction outright -- it cannot replay a block
        // it does not own. Wrapping the whole unit in the strategy is what makes the retry
        // and the transaction coexist.
        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (shouldBeDefault)
            {
                var current = await db.TaxClasses
                    .FirstOrDefaultAsync(t => t.IsDefault && t.Id != taxClass.Id, cancellationToken);

                if (current is not null)
                {
                    current.IsDefault = false;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }

            taxClass.IsDefault = shouldBeDefault;

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception)
                when (PostgresErrors.IsUniqueViolation(exception, DefaultConstraint))
            {
                // The transaction does not close this race: two creates that both arrive
                // with isDefault and find nothing to clear will both insert, and the index
                // rejects the second. That is a conflict the caller can act on by re-reading
                // and promoting, so it is a 409 rather than an unhandled 500.
                throw new DefaultTaxClassConflictException(
                    "Another tax class became the default while this one was being saved.",
                    exception);
            }

            await transaction.CommitAsync(cancellationToken);
        });
    }

    /// <summary>
    /// Checks every field before returning, so a request that gets two of them wrong is told
    /// about both rather than one at a time.
    /// </summary>
    private static bool TryValidate(
        string? name,
        decimal? rate,
        out string validatedName,
        out Dictionary<string, string[]> errors)
    {
        errors = [];
        validatedName = name?.Trim() ?? string.Empty;

        if (validatedName.Length is 0 or > TaxClass.NameMaxLength)
        {
            errors["name"] = [$"A name of 1 to {TaxClass.NameMaxLength} characters is required."];
        }

        if (rate is not { } value)
        {
            // Required rather than defaulted to zero. A tax class created with no rate would
            // be a silently zero-rated one, and every product priced against it would
            // undercharge tax until somebody reconciled a return.
            errors["rate"] = ["A rate is required."];
        }
        else if (!CatalogRules.IsValidTaxRate(value))
        {
            // The wording names the fraction explicitly because entering 20 for "20%" is the
            // single most likely mistake anyone makes on this screen.
            errors["rate"] = [
                "A rate between 0 and 1 with at most 4 decimal places is required — 20% is 0.2000.",
            ];
        }

        return errors.Count == 0;
    }

    /// <summary>
    /// The projection the list endpoint pushes into SQL. A field-for-field twin of
    /// <see cref="Map"/>, kept separate because one must be an expression tree.
    /// </summary>
    private static System.Linq.Expressions.Expression<Func<TaxClass, TaxClassResponse>> Project =>
        t => new TaxClassResponse(t.Id, t.Name, t.Rate, t.IsDefault, t.CreatedAt, t.UpdatedAt);

    private static TaxClassResponse Map(TaxClass taxClass) => new(
        taxClass.Id,
        taxClass.Name,
        taxClass.Rate,
        taxClass.IsDefault,
        taxClass.CreatedAt,
        taxClass.UpdatedAt);
}
