using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Idempotency;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;
using Pos.Core.Sales;
using Pos.Core.Tenders;
using Pos.Data;

namespace Pos.Api.Endpoints;

/// <summary>One line of a cart, as a client sends it.</summary>
/// <remarks>
/// Nullable throughout for the reason every create-request in this API is: an omitted
/// <c>decimal</c> binds to zero, and a zero quantity that reported success would put a line on
/// a receipt for nothing.
/// </remarks>
public sealed record SaleLineRequest(
    Guid? ProductId,
    decimal? Quantity,
    decimal? UnitPriceOverride,
    decimal? DiscountAmount);

/// <summary>A tender offered against a sale.</summary>
public sealed record SaleTenderRequest(string? Method, decimal? Amount, string? Reference);

/// <summary>
/// A cart to price or to sell. <c>POST /sales/quote</c> ignores <c>tenders</c>.
/// </summary>
/// <remarks>
/// <b>No totals.</b> The server computes every amount, and a client-sent total is not merely
/// distrusted — there is nowhere to put one. A price a client can send is a price a customer
/// can edit.
/// </remarks>
public sealed record CreateSaleRequest(
    Guid? ClientTransactionId,
    Guid? RegisterId,
    Guid? ShiftId,
    IReadOnlyList<SaleLineRequest>? Lines,
    decimal? CartDiscountAmount,
    IReadOnlyList<SaleTenderRequest>? Tenders);

/// <summary>One priced line, as the register displays and the receipt prints it.</summary>
public sealed record SaleLineResponse(
    Guid Id,
    Guid ProductId,
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal TaxRate,
    decimal DiscountAmount,
    decimal LineSubtotal,
    decimal LineTax,
    decimal LineTotal,
    bool IsPriceOverridden);

/// <summary>A tender as recorded.</summary>
public sealed record SaleTenderResponse(TenderMethod Method, decimal Amount, decimal? ChangeGiven);

/// <summary>
/// A priced cart. <c>POST /sales/quote</c> returns this with the sale-identity fields null.
/// </summary>
public sealed record SaleResponse(
    Guid? Id,
    long? SaleNumber,
    SaleType Type,
    SaleStatus? Status,
    decimal Subtotal,
    decimal DiscountTotal,
    decimal TaxTotal,
    decimal RoundingAdjustment,
    decimal Total,
    decimal ChangeGiven,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<SaleLineResponse> Lines,
    IReadOnlyList<SaleTenderResponse> Tenders);

/// <summary>A sale as the history list shows it, without its lines.</summary>
public sealed record SaleSummaryResponse(
    Guid Id,
    long SaleNumber,
    SaleType Type,
    SaleStatus Status,
    Guid RegisterId,
    Guid ShiftId,
    Guid CashierId,
    decimal Total,
    DateTimeOffset CompletedAt);

public static class SaleEndpoints
{
    private const string Sort = "sale:completed";

    public static IEndpointRouteBuilder MapSaleEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var sales = builder.MapGroup("/api/v1/sales").WithTags("Sales");

        sales.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Sale history");

        sales.MapGet("/{id:guid}", GetAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("One sale with its lines and tenders");

        // Priced but not committed, so no idempotency key: nothing is written, and a replay is
        // simply the same arithmetic again.
        sales.MapPost("/quote", QuoteAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Price a cart without committing it");

        sales.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanSell)
            .RequireIdempotency()
            .WithSummary("Complete a sale");

        return builder;
    }

    /// <summary>
    /// Every sale, regardless of type or status.
    /// </summary>
    /// <remarks>
    /// Voids and refunds are <b>not</b> hidden by default. A list that quietly omitted them is
    /// how a manager fails to find the transaction they are looking for and concludes the
    /// system lost it.
    /// </remarks>
    private static async Task<Results<Ok<CursorPage<SaleSummaryResponse>>, ValidationProblem>> ListAsync(
        AppDbContext db,
        string? cursor,
        int? limit,
        Guid? registerId,
        Guid? shiftId,
        Guid? cashierId,
        CancellationToken cancellationToken)
    {
        if (!PageQuery.TryRead<DateTimeOffset>(cursor, limit, Sort, out var page, out var errors))
        {
            return TypedResults.ValidationProblem(errors);
        }

        var query = db.Sales.AsNoTracking();

        if (registerId is { } register)
        {
            query = query.Where(s => s.RegisterId == register);
        }

        if (shiftId is { } shift)
        {
            query = query.Where(s => s.ShiftId == shift);
        }

        if (cashierId is { } cashier)
        {
            query = query.Where(s => s.CashierId == cashier);
        }

        var results = await query.ToPageAsync(
            s => s.CompletedAt,
            s => new SaleSummaryResponse(
                s.Id,
                s.SaleNumber,
                s.Type,
                s.Status,
                s.RegisterId,
                s.ShiftId,
                s.CashierId,
                (decimal)s.Total,
                s.CompletedAt),
            page,
            cancellationToken);

        return TypedResults.Ok(results);
    }

    private static async Task<Results<Ok<SaleResponse>, NotFound>> GetAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var sale = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (sale is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await ReadAsync(db, sale, cancellationToken));
    }

    private static async Task<Results<Ok<SaleResponse>, ValidationProblem>> QuoteAsync(
        CreateSaleRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (cart, errors, _) = await BuildCartAsync(db, request, quoting: true, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var priced = PricingEngine.Price(cart!);

        return TypedResults.Ok(Project(priced, Money.Zero));
    }

    /// <summary>
    /// Prices the cart and commits it, in one transaction, exactly once.
    /// </summary>
    private static async Task<Results<Created<SaleResponse>, ValidationProblem, ForbidHttpResult>> CreateAsync(
        CreateSaleRequest request,
        AppDbContext db,
        ISaleWriter writer,
        IIdempotencyContext idempotency,
        System.Security.Claims.ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (cart, errors, tracked) = await BuildCartAsync(db, request, quoting: false, cancellationToken);

        // Discounts and overrides are separately gated, and a Cashier sending one is refused
        // rather than silently ignored: unlike an unreadable costPrice, this changes what the
        // customer pays, so quietly dropping it would take the shop's money instead of theirs.
        if (cart is not null && NeedsDiscountPermission(cart) &&
            !(await authorization.AuthorizeAsync(caller, Policies.CanApplyDiscount)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        if (cart is not null && cart.Lines.Any(l => l.IsPriceOverridden) &&
            !(await authorization.AuthorizeAsync(caller, Policies.CanOverridePrice)).Succeeded)
        {
            return TypedResults.Forbid();
        }

        ValidateTenders(request, errors);
        await ValidateShiftAsync(db, request, errors, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var priced = PricingEngine.Price(cart!);

        var tenders = (request.Tenders ?? [])
            .Select(t => new TenderInstruction(
                Enum.Parse<TenderMethod>(t.Method!, ignoreCase: false),
                (Money)t.Amount!.Value,
                t.Reference))
            .ToArray();

        if (!TenderRules.IsSufficient(priced.Total, [.. tenders.Select(t => t.Amount)]))
        {
            // Thrown rather than returned as a field error: it is a 409 about the sale as a
            // whole, not a complaint about one input. See UnderTenderException.
            throw new Core.Exceptions.UnderTenderException(
                $"The sale comes to {priced.Total} and "
                + $"{Money.Sum(tenders.Select(t => t.Amount))} was tendered.");
        }

        SaleResponse? response = null;

        var result = await writer.CommitAsync(
            new SaleCommitRequest(
                request.ClientTransactionId!.Value,
                request.RegisterId!.Value,
                request.ShiftId!.Value,
                cart!.TaxMode,
                priced,
                tenders,
                tracked),

            // Runs inside the writer's transaction, once the numbers are known. This is what
            // makes "the key is inserted in the same transaction as the work" true rather than
            // approximately true.
            committed =>
            {
                response = Project(priced, committed.ChangeGiven, committed, tenders);
                idempotency.Record(db, StatusCodes.Status201Created, response, committed.CompletedAt);
            },
            cancellationToken);

        return TypedResults.Created($"/api/v1/sales/{result.SaleId}", response!);
    }

    /// <summary>
    /// Turns a request into a <see cref="Cart"/>, resolving every product against the catalog.
    /// </summary>
    /// <remarks>
    /// <b>One builder, both routes.</b> This is the mechanical reason a quote and the sale that
    /// follows it cannot disagree — not a promise that two code paths were kept in step. Two
    /// implementations of tax and discount rules will differ eventually, and the place it
    /// surfaces is a customer disputing a receipt at a counter.
    /// </remarks>
    private static async Task<(Cart? Cart, Dictionary<string, string[]> Errors, IReadOnlySet<Guid> Tracked)>
        BuildCartAsync(
            AppDbContext db,
            CreateSaleRequest request,
            bool quoting,
            CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        if (!quoting)
        {
            if (request.ClientTransactionId is not { } clientTransactionId || clientTransactionId == Guid.Empty)
            {
                errors["clientTransactionId"] = ["A client transaction id is required."];
            }

            if (request.RegisterId is not { } register || register == Guid.Empty)
            {
                errors["registerId"] = ["A register is required."];
            }

            if (request.ShiftId is not { } shift || shift == Guid.Empty)
            {
                errors["shiftId"] = ["A shift is required."];
            }
        }

        if (request.Lines is not { Count: > 0 })
        {
            // Refused at the endpoint, not in the engine: the engine prices an empty cart to
            // zero quite happily, and it is right to. A sale of nothing is a UI slip.
            errors["lines"] = ["At least one line is required."];
            return (null, errors, new HashSet<Guid>());
        }

        var productIds = request.Lines
            .Where(l => l.ProductId is not null)
            .Select(l => l.ProductId!.Value)
            .Distinct()
            .ToArray();

        // One read for the whole cart, joined to the tax class so a line is priced without a
        // second round trip per item — the same reasoning as the barcode lookup.
        var products = await (
            from product in db.Products.AsNoTracking()
            join taxClass in db.TaxClasses on product.TaxClassId equals taxClass.Id
            where productIds.Contains(product.Id)
            select new
            {
                product.Id,
                product.Name,
                product.UnitPrice,
                product.IsActive,
                product.TrackStock,
                taxClass.Rate,
            }).ToDictionaryAsync(p => p.Id, cancellationToken);

        var lines = new List<CartLine>();
        var tracked = new HashSet<Guid>();

        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];

            if (line.ProductId is not { } productId || !products.TryGetValue(productId, out var product))
            {
                // 400 on the field rather than 404, and the same answer whether the id is
                // unknown or belongs to another shop — so it is not an existence oracle.
                errors[$"lines[{index}].productId"] = ["No product with that id exists in this tenant."];
                continue;
            }

            if (!product.IsActive)
            {
                // A deactivated product still scans, so the register can say "not for sale".
                // Selling one is a different question, and the answer is no.
                errors[$"lines[{index}].productId"] = ["That product is not for sale."];
                continue;
            }

            if (line.Quantity is not { } quantity || quantity <= 0m)
            {
                errors[$"lines[{index}].quantity"] = ["A quantity greater than zero is required."];
                continue;
            }

            if (!Core.Catalog.CatalogRules.IsStorableAmount(quantity))
            {
                errors[$"lines[{index}].quantity"] = ["A quantity with at most 4 decimal places is required."];
                continue;
            }

            var overridden = line.UnitPriceOverride is not null;

            if (line.UnitPriceOverride is { } price && !Core.Catalog.CatalogRules.IsStorableAmount(price))
            {
                errors[$"lines[{index}].unitPriceOverride"] = ["A price of 0 or more with at most 4 decimal places is required."];
                continue;
            }

            var discount = line.DiscountAmount ?? 0m;

            if (!Core.Catalog.CatalogRules.IsStorableAmount(discount))
            {
                errors[$"lines[{index}].discountAmount"] = ["A discount of 0 or more with at most 4 decimal places is required."];
                continue;
            }

            lines.Add(new CartLine(
                productId,
                product.Name,
                quantity,
                overridden ? (Money)line.UnitPriceOverride!.Value : product.UnitPrice,
                product.Rate,
                (Money)discount,
                overridden));

            if (product.TrackStock)
            {
                tracked.Add(productId);
            }
        }

        var cartDiscount = request.CartDiscountAmount ?? 0m;

        if (!Core.Catalog.CatalogRules.IsStorableAmount(cartDiscount))
        {
            errors["cartDiscountAmount"] = ["A discount of 0 or more with at most 4 decimal places is required."];
        }

        if (errors.Count > 0)
        {
            return (null, errors, tracked);
        }

        // The tenant's own settings, read once. TaxMode is snapshotted onto the sale so no
        // report ever has to come back here for it.
        var settings = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == db.CurrentTenantId)
            .Select(t => new { t.TaxMode, t.CashRoundingIncrement })
            .FirstAsync(cancellationToken);

        return (
            new Cart(lines, (Money)cartDiscount, settings.TaxMode, settings.CashRoundingIncrement),
            errors,
            tracked);
    }

    /// <summary>
    /// Checks the shift exists here, belongs to the named register, and is open.
    /// </summary>
    /// <remarks>
    /// Three different answers for three different situations, and the distinction is what the
    /// cashier does next:
    /// <list type="bullet">
    /// <item>Unknown or another tenant's shift → <b>400 on the field</b>, identically either
    /// way so it is not an existence oracle. It is a client bug.</item>
    /// <item>A shift belonging to a different register → <b>400 on <c>registerId</c></b>. Two
    /// ids for one fact, and a sale on the wrong drawer makes a Z-report reconcile the wrong
    /// till.</item>
    /// <item>A real, matching, <i>closed</i> shift → <b>409</b>. Nothing is wrong with the
    /// request; the register simply is not trading, and the answer is to open one.</item>
    /// </list>
    /// <para>
    /// The writer repeats the open check under a row lock, and that is not redundant: this
    /// read is unlocked, so a close can land between the two. Here it produces a good error
    /// message; there it is the guarantee.
    /// </para>
    /// </remarks>
    private static async Task ValidateShiftAsync(
        AppDbContext db,
        CreateSaleRequest request,
        Dictionary<string, string[]> errors,
        CancellationToken cancellationToken)
    {
        if (request.ShiftId is not { } shiftId || shiftId == Guid.Empty || errors.ContainsKey("shiftId"))
        {
            return;
        }

        var shift = await db.Shifts
            .AsNoTracking()
            .Where(s => s.Id == shiftId)
            .Select(s => new { s.RegisterId, s.Status })
            .FirstOrDefaultAsync(cancellationToken);

        if (shift is null)
        {
            errors["shiftId"] = ["No shift with that id exists in this tenant."];
            return;
        }

        if (request.RegisterId is { } registerId && shift.RegisterId != registerId)
        {
            errors["registerId"] = ["That shift belongs to a different register."];
            return;
        }

        if (shift.Status != ShiftStatus.Open)
        {
            throw new Core.Exceptions.ShiftClosedException();
        }
    }

    private static bool NeedsDiscountPermission(Cart cart) =>
        !cart.CartDiscount.IsZero || cart.Lines.Any(l => !l.LineDiscount.IsZero);

    private static void ValidateTenders(CreateSaleRequest request, Dictionary<string, string[]> errors)
    {
        if (request.Tenders is not { Count: > 0 })
        {
            errors["tenders"] = ["At least one tender is required."];
            return;
        }

        for (var index = 0; index < request.Tenders.Count; index++)
        {
            var tender = request.Tenders[index];

            if (!Enum.TryParse<TenderMethod>(tender.Method, ignoreCase: false, out var method)
                || !Enum.IsDefined(method)
                || !TenderRules.IsAccepted(method))
            {
                // Declared is not accepted: TenderMethod carries values the MVP does not take,
                // and a Card tender no processor ever saw would sit in the takings reconciling
                // against nothing.
                errors[$"tenders[{index}].method"] =
                    [$"One of {string.Join(", ", TenderRules.AcceptedMethods)} is required."];
            }

            if (tender.Amount is not { } amount)
            {
                errors[$"tenders[{index}].amount"] = ["An amount is required."];
            }
            else if (amount <= 0m || !Core.Catalog.CatalogRules.IsStorableAmount(amount))
            {
                errors[$"tenders[{index}].amount"] =
                    ["An amount greater than zero with at most 4 decimal places is required."];
            }
        }
    }

    /// <summary>Projects a priced cart, with or without the identity a committed sale has.</summary>
    private static SaleResponse Project(
        PricedSale priced,
        Money change,
        SaleCommitResult? committed = null,
        IReadOnlyList<TenderInstruction>? tenders = null) =>
        new(
            committed?.SaleId,
            committed?.SaleNumber,
            SaleType.Sale,
            committed is null ? null : SaleStatus.Completed,
            (decimal)priced.Subtotal,
            (decimal)priced.DiscountTotal,
            (decimal)priced.TaxTotal,
            (decimal)priced.RoundingAdjustment,
            (decimal)priced.Total,
            (decimal)change,
            committed?.CompletedAt,
            [.. priced.Lines.Select((line, index) => new SaleLineResponse(
                committed is null ? Guid.Empty : committed.LineIds[index],
                line.Source.ProductId,
                line.LineNumber,
                line.Source.Description,
                line.Source.Quantity,
                (decimal)line.Source.UnitPrice,
                line.Source.TaxRate,
                (decimal)line.Discount,
                (decimal)line.Subtotal,
                (decimal)line.Tax,
                (decimal)line.Total,
                line.Source.IsPriceOverridden))],
            [.. (tenders ?? []).Select((t, index) => new SaleTenderResponse(
                t.Method,
                (decimal)t.Amount,
                index == 0 && !change.IsZero ? (decimal)change : null))]);

    /// <summary>Reads a stored sale back, from its own rows and never from the catalog.</summary>
    private static async Task<SaleResponse> ReadAsync(
        AppDbContext db,
        Sale sale,
        CancellationToken cancellationToken)
    {
        var lines = await db.SaleLines
            .AsNoTracking()
            .Where(l => l.SaleId == sale.Id)
            .OrderBy(l => l.LineNumber)
            .Select(l => new SaleLineResponse(
                l.Id,
                l.ProductId,
                l.LineNumber,

                // The snapshots, read from the sale line. A join to Product here would let a
                // price change rewrite this receipt — invariant 5, at the read path.
                l.Description,
                l.Quantity,
                (decimal)l.UnitPrice,
                l.TaxRate,
                (decimal)l.DiscountAmount,
                (decimal)l.LineSubtotal,
                (decimal)l.LineTax,
                (decimal)l.LineTotal,
                l.IsPriceOverridden))
            .ToListAsync(cancellationToken);

        var tenders = await db.Tenders
            .AsNoTracking()
            .Where(t => t.SaleId == sale.Id)
            .Select(t => new SaleTenderResponse(t.Method, (decimal)t.Amount, (decimal?)t.ChangeGiven))
            .ToListAsync(cancellationToken);

        return new SaleResponse(
            sale.Id,
            sale.SaleNumber,
            sale.Type,
            sale.Status,
            (decimal)sale.Subtotal,
            (decimal)sale.DiscountTotal,
            (decimal)sale.TaxTotal,
            (decimal)sale.RoundingAdjustment,
            (decimal)sale.Total,
            tenders.Sum(t => t.ChangeGiven ?? 0m),
            sale.CompletedAt,
            lines,
            tenders);
    }
}
