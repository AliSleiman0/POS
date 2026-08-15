using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Common;
using Pos.Api.Idempotency;
using Pos.Api.Observability;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;
using Pos.Core.Receipts;
using Pos.Core.Reporting;
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
/// <para>
/// <c>occurredAt</c> is the sole exception to "the client sends inputs, the server decides
/// facts", and it is one only because a sale queued offline has no other way to be dated. It
/// is bounded by <see cref="OfflineSaleRules"/>, it never touches an amount, and the server
/// stamps <c>RecordedAt</c> beside it regardless.
/// </para>
/// </remarks>
public sealed record CreateSaleRequest(
    Guid? ClientTransactionId,
    Guid? RegisterId,
    Guid? ShiftId,
    IReadOnlyList<SaleLineRequest>? Lines,
    decimal? CartDiscountAmount,
    IReadOnlyList<SaleTenderRequest>? Tenders,

    /// <summary>
    /// When the customer paid, for a sale rung offline and sent later. Omit it online.
    /// </summary>
    /// <remarks>
    /// <b>Mint it once, when the sale completes, and send that same value on every attempt.</b>
    /// It is part of the request body, so it is part of the fingerprint
    /// <c>IdempotencyFilter</c> takes — a client that re-reads its clock on each retry sends a
    /// different body under the same key and is answered <c>409 idempotency-key-reused</c>
    /// for ever, which reads exactly like a sale that will not go through.
    /// </remarks>
    DateTimeOffset? OccurredAt = null,

    /// <summary>
    /// Cash the customer is leaving for the staff, over and above the total.
    /// </summary>
    /// <remarks>
    /// <b>Never added to the total.</b> It reduces the change given, which is what leaves it in
    /// the drawer for the shift's expected-cash sum to find — see <c>Sale.TipAmount</c>. Null
    /// and zero mean the same thing here, unlike <c>countedCash</c> on a shift close: a tip
    /// nobody mentioned is a tip nobody left, and there is no reading of an absent value that
    /// costs anybody money.
    /// </remarks>
    decimal? Tip = null);

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
/// A refund made against a sale, as the original sale's detail lists it.
/// </summary>
/// <remarks>
/// <b>The half of the link that is easy to leave out.</b> A refund knows its original through
/// <c>OriginalSaleId</c>; without the reverse direction, somebody looking at a sale a customer
/// has brought back cannot see it was already refunded — and refunds it again.
/// </remarks>
public sealed record LinkedRefundResponse(
    Guid Id,
    long SaleNumber,
    decimal Total,
    DateTimeOffset CompletedAt);

/// <summary>
/// A priced cart. <c>POST /sales/quote</c> returns this with the sale-identity fields null.
/// </summary>
/// <remarks>
/// The fields below <c>Tenders</c> arrived with Phase 6.4's history detail and are all nullable
/// or empty for a quote, which has no sale behind it to answer them.
/// </remarks>
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
    IReadOnlyList<SaleTenderResponse> Tenders,
    Guid? RegisterId = null,
    Guid? ShiftId = null,
    Guid? CashierId = null,

    /// <summary>Current values, like a receipt's. A renamed cashier is the same person.</summary>
    string? CashierName = null,
    string? RegisterName = null,
    TaxMode? TaxMode = null,
    Guid? OriginalSaleId = null,
    long? OriginalSaleNumber = null,
    DateTimeOffset? VoidedAt = null,
    string? VoidedByName = null,
    string? VoidReason = null,
    string? RefundReason = null,

    /// <summary>Refunds written against this sale. Empty on a refund and on a quote.</summary>
    IReadOnlyList<LinkedRefundResponse>? Refunds = null,

    /// <summary>
    /// When the server wrote the row, as against <c>completedAt</c>'s when the trade happened.
    /// Null on a quote, which has no row.
    /// </summary>
    /// <remarks>
    /// Equal to <c>completedAt</c> for anything rung online. The gap between them is how a
    /// client tells an offline sale from an ordinary one without a flag it would have to be
    /// trusted to set — and it is what the reconciliation screen shows when it has to explain
    /// why a drawer counted at 18:00 did not include a sale taken at 17:40.
    /// </remarks>
    DateTimeOffset? RecordedAt = null,

    /// <summary>
    /// Cash left for the staff, over and above <c>total</c>. Zero for most sales.
    /// </summary>
    /// <remarks>
    /// Sent so a receipt and a Z-report can show it on its own line. A drawer that is over by
    /// exactly the evening's tips has to <i>read</i> as that, rather than as an unexplained
    /// surplus somebody has to reconstruct.
    /// </remarks>
    decimal TipAmount = 0m);

/// <summary>Why a sale is being reversed. Required — see the endpoint.</summary>
public sealed record VoidSaleRequest(string? Reason);

/// <summary>One line being returned, named by its sale line rather than its product.</summary>
public sealed record RefundLineRequest(Guid? SaleLineId, decimal? Quantity);

/// <summary>
/// A return against a completed sale. Omitting <c>lines</c> refunds everything still owing.
/// </summary>
/// <remarks>
/// The shift and register are the <b>current</b> ones, not the original sale's: the cash comes
/// out of the drawer that is open now, which is where it physically is.
/// </remarks>
public sealed record RefundSaleRequest(
    Guid? ClientTransactionId,
    Guid? RegisterId,
    Guid? ShiftId,
    string? Reason,
    IReadOnlyList<RefundLineRequest>? Lines);

/// <summary>A sale as the history list shows it, without its lines.</summary>
/// <remarks>
/// <c>CashierName</c> and <c>RegisterName</c> are here rather than left to the client because a
/// list of GUIDs is not a history anybody can read, and a client that resolved them itself would
/// issue one request per row.
/// </remarks>
public sealed record SaleSummaryResponse(
    Guid Id,
    long SaleNumber,
    SaleType Type,
    SaleStatus Status,
    Guid RegisterId,
    Guid ShiftId,
    Guid CashierId,
    string CashierName,
    string RegisterName,
    Guid? OriginalSaleId,
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

        // Ordered before nothing and colliding with nothing: "by-client-transaction" is not a
        // guid, so the {id:guid} route above cannot match it.
        sales.MapGet("/by-client-transaction/{clientTransactionId:guid}", GetByClientTransactionAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("The sale a client transaction id produced, if it produced one");

        // CanSell, like the sale itself: handing a customer their receipt is the last step of
        // serving them, and a reprint is asked for at a counter by whoever is standing there.
        sales.MapGet("/{id:guid}/receipt", ReceiptAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("The receipt payload for a sale, rendered server-side");

        // Priced but not committed, so no idempotency key: nothing is written, and a replay is
        // simply the same arithmetic again.
        sales.MapPost("/quote", QuoteAsync)
            .RequireAuthorization(Policies.CanSell)
            .WithSummary("Price a cart without committing it");

        sales.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanSell)
            // The one metric worth an alert: a till that cannot take money. A filter rather
            // than lines inside the handler because CreateAsync has a dozen exit paths and
            // the one that matters most is the exception nobody anticipated.
            //
            // BEFORE RequireIdempotency, and the order is the point: filters run outermost
            // first, so registering this second would put it inside the idempotency filter
            // — which short-circuits a replay without ever calling in. Every retried sale
            // would go uncounted, during exactly the network trouble that caused the retry.
            .AddEndpointFilter<SaleSubmissionMetricsFilter>()
            .RequireIdempotency()
            .WithSummary("Complete a sale");

        sales.MapPost("/{id:guid}/void", VoidAsync)
            .RequireAuthorization(Policies.CanVoidSale)
            .RequireIdempotency()
            .WithSummary("Void a sale and put its stock back");

        sales.MapPost("/{id:guid}/refund", RefundAsync)
            .RequireAuthorization(Policies.CanRefund)
            .RequireIdempotency()
            .WithSummary("Refund all or part of a sale");

        // There is deliberately no PUT and no DELETE, and there never will be. A completed
        // sale is append-only: corrections are the two routes above, which write new rows and
        // a status flag. NoRouteUpdatesOrDeletesASale enumerates the routing table and fails
        // the build if one ever appears.
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
        string? from,
        string? to,
        string? type,
        string? status,
        long? saleNumber,
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

        /*
         * `from` and `to` are trading days, resolved exactly as the daily report resolves its
         * `?date=` — through the tenant's zone and day-start offset, not UTC midnight.
         *
         * Both ends inclusive, because "7th to 7th" plainly means that one day. A shop with a
         * 04:00 day start otherwise finds its history and its daily report disagreeing by a few
         * hours' sales, which is how staff stop trusting both.
         */
        if (from is not null || to is not null)
        {
            var shop = await ReportEndpoints.ShopAsync(db, cancellationToken);
            var zone = TenantTimeZone.Resolve(shop.TimeZoneId);

            if (TryReadDay(from, "from", errors, out var firstDay))
            {
                var start = BusinessDay.Range(firstDay, zone, shop.BusinessDayStartOffset).StartUtc;
                query = query.Where(s => s.CompletedAt >= start);
            }

            if (TryReadDay(to, "to", errors, out var lastDay))
            {
                var end = BusinessDay.Range(lastDay, zone, shop.BusinessDayStartOffset).EndUtc;
                query = query.Where(s => s.CompletedAt < end);
            }
        }

        // Parsed rather than bound, so an unknown value is a 400 naming the field instead of a
        // silently ignored filter — a history quietly showing more than was asked for is a
        // manager concluding the shop sold things it did not.
        if (type is not null)
        {
            if (Enum.TryParse<SaleType>(type, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
            {
                query = query.Where(s => s.Type == parsed);
            }
            else
            {
                errors["type"] = [$"One of {string.Join(", ", Enum.GetNames<SaleType>())} is required."];
            }
        }

        if (status is not null)
        {
            if (Enum.TryParse<SaleStatus>(status, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed))
            {
                query = query.Where(s => s.Status == parsed);
            }
            else
            {
                errors["status"] = [$"One of {string.Join(", ", Enum.GetNames<SaleStatus>())} is required."];
            }
        }

        // The reference a customer reads off their receipt, and therefore the only search that
        // matters at a counter. Index-backed by ux_sale_tenant_sale_number.
        if (saleNumber is { } number)
        {
            query = query.Where(s => s.SaleNumber == number);
        }

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
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

                // Left joins in LINQ form, so a cashier or register that has since been removed
                // leaves a readable row rather than dropping the sale out of its own history.
                db.Users.Where(u => u.Id == s.CashierId).Select(u => u.DisplayName).FirstOrDefault()
                    ?? "Unknown",
                db.Registers.Where(r => r.Id == s.RegisterId).Select(r => r.Name).FirstOrDefault()
                    ?? "Unknown",
                s.OriginalSaleId,
                (decimal)s.Total,
                s.CompletedAt),
            page,
            cancellationToken,

            // Newest first, unlike every other list in this API. A history whose first page is
            // the shop's very first sales is unusable at a counter: the transaction anybody is
            // looking for happened today, and the stock ledger's oldest-first rule is for a
            // different question — "why is this number wrong?" is read forwards.
            descending: true);

        return TypedResults.Ok(results);
    }

    /// <summary>
    /// Reads a <c>yyyy-MM-dd</c> filter bound, recording a field error if it is not one.
    /// </summary>
    /// <remarks>
    /// Absent is not an error — it means "no bound". A malformed one <b>is</b>, rather than
    /// being dropped: a date filter that silently does nothing returns more history than was
    /// asked for, and every row in it looks legitimate.
    /// </remarks>
    /// <summary>
    /// Reads a <c>?from=</c>/<c>?to=</c> trading day, or files a field error.
    /// </summary>
    /// <remarks>
    /// <c>internal</c> so <c>AuditEndpoints</c> reuses it rather than growing a second date
    /// parser. Two of them would eventually differ on what a trading day is, and the place that
    /// surfaces is an owner comparing the audit log against the day's report.
    /// </remarks>
    internal static bool TryReadDay(
        string? value,
        string field,
        Dictionary<string, string[]> errors,
        out DateOnly day)
    {
        day = default;

        if (value is null)
        {
            return false;
        }

        if (DateOnly.TryParseExact(
                value,
                "yyyy-MM-dd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out day))
        {
            return true;
        }

        errors[field] = ["A date in the form 2026-08-07 is required."];
        return false;
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

    /// <summary>
    /// The sale a client transaction id produced, or 404 if it never produced one.
    /// </summary>
    /// <remarks>
    /// <b>This exists so a till can find out what happened to a sale it lost the answer to.</b>
    /// A register that reloads mid-payment holds the GUID it submitted and nothing else: the
    /// basket may have been charged, or the request may never have arrived, and those two need
    /// opposite actions from the cashier.
    /// <para>
    /// The alternative is re-POSTing the sale and letting idempotency answer — which works when
    /// the answer is "it landed" and <i>takes the money</i> when it is not, on a page load, with
    /// nobody having pressed anything. A read is the honest question.
    /// </para>
    /// <para>
    /// Not an existence oracle across tenants: the query filter scopes it, so another tenant's
    /// id is a 404 indistinguishable from an unused one.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<SaleResponse>, NotFound>> GetByClientTransactionAsync(
        Guid clientTransactionId,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        // Index-backed and unique: ux_sale_tenant_client_transaction_id in SaleConfiguration.
        var sale = await db.Sales
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.ClientTransactionId == clientTransactionId, cancellationToken);

        if (sale is null)
        {
            return TypedResults.NotFound();
        }

        return TypedResults.Ok(await ReadAsync(db, sale, cancellationToken));
    }

    /// <summary>
    /// The receipt for a sale — the same payload for a first print and for a reprint.
    /// </summary>
    /// <remarks>
    /// <b>Rendered here, not in the browser.</b> Browser printing consumes this today, a
    /// thermal printer through the desktop app will consume it later, and email later still.
    /// Three independent renderers guarantee three subtly different receipts, and the one a tax
    /// authority looks at is the wrong one.
    /// <para>
    /// Every amount and description comes from the sale's own rows. <see cref="ReceiptSource"/>
    /// carries no product, so there is nothing here to join a current price to — invariant 5
    /// made structural rather than remembered.
    /// </para>
    /// <para>
    /// It is a pure read and takes no idempotency key: nothing is written, and in particular
    /// <b>no count of copies is kept</b>. Marking a reprint is the client's job today; see
    /// DECISIONS.md for why server-side counting waits for Phase 7.2's audit log rather than
    /// being half-built here as a mutable column on an append-only sale.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>A GET that writes</b>, deliberately, and the one place in this API where that is
    /// true. Each issue appends a <c>ReceiptIssued</c> audit entry, and the reprint mark is
    /// derived from how many came before — so a client can no longer print an unmarked
    /// duplicate by declining to send a flag, which is the refund-fraud vector §6.2 recorded
    /// and DECISIONS.md left owing. What is recorded is that the receipt was *disclosed*;
    /// opening the preview discloses it, so a look without a print marks the next copy as a
    /// reprint. That is the safe direction for a control of this kind.
    /// </remarks>
    private static async Task<Results<Ok<ReceiptResponse>, NotFound>> ReceiptAsync(
        Guid id,
        AppDbContext db,
        IAuditLog audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var sale = await db.Sales.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);

        if (sale is null)
        {
            return TypedResults.NotFound();
        }

        var lines = await db.SaleLines
            .AsNoTracking()
            .Where(l => l.SaleId == sale.Id)
            .ToListAsync(cancellationToken);

        var tenders = await db.Tenders
            .AsNoTracking()
            .Where(t => t.SaleId == sale.Id)
            .ToListAsync(cancellationToken);

        // Not filtered by tenant in LINQ because Tenant is not tenant-owned — it is the tenant
        // list. CurrentTenantId comes from the validated token, as everywhere else.
        var shop = await db.Tenants
            .AsNoTracking()
            .FirstAsync(t => t.Id == db.CurrentTenantId, cancellationToken);

        // Current values, deliberately. A renamed cashier is the same person, and invariant 5
        // is about amounts. The fallbacks cover a user or register that has since been removed:
        // a receipt that cannot print because somebody left the company is worse than one that
        // says the name is unknown.
        var cashierName = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == sale.CashierId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        var registerName = await db.Registers
            .AsNoTracking()
            .Where(r => r.Id == sale.RegisterId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(cancellationToken) ?? "Unknown";

        // The number, not the id: it is printed for a human to quote back over a counter.
        long? originalSaleNumber = null;

        if (sale.OriginalSaleId is { } originalId)
        {
            var original = await db.Sales
                .AsNoTracking()
                .Where(s => s.Id == originalId)
                .Select(s => (long?)s.SaleNumber)
                .FirstOrDefaultAsync(cancellationToken);

            originalSaleNumber = original;
        }

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(sale, lines, tenders, shop, cashierName, registerName, originalSaleNumber),
            TenantTimeZone.Resolve(shop.TimeZoneId),
            timeProvider.GetUtcNow());

        // Counted before this issue is written, so the first copy is number 1.
        //
        // Two simultaneous requests can both count the same number and both call themselves
        // originals. Left as is: a unique index would refuse the second issue outright, and
        // refusing to print a receipt a customer is waiting for is a worse failure than two
        // rows saying "first". Both are still recorded, which is what the log is for.
        var issueNumber = 1 + await db.AuditEntries
            .CountAsync(
                a => a.Action == AuditAction.ReceiptIssued && a.EntityId == sale.Id,
                cancellationToken);

        await audit.RecordStandaloneAsync(
            AuditAction.ReceiptIssued,
            nameof(Sale),
            sale.Id,
            after: new Dictionary<string, string?>
            {
                ["issueNumber"] = issueNumber.ToString(CultureInfo.InvariantCulture),
                ["saleNumber"] = sale.SaleNumber.ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken);

        return TypedResults.Ok(
            ReceiptResponse.From(receipt, sale.Id, sale.OriginalSaleId, issueNumber));
    }

    /// <summary>
    /// Prices a cart without committing it.
    /// </summary>
    /// <remarks>
    /// <b>The discount and override policies are enforced here too</b>, even though nothing is
    /// written. A quote that priced a discount the sale would then refuse would put a total on
    /// the customer-facing display that the till cannot honour — the cashier reads it out, the
    /// customer counts out the money, and only then does the sale come back 403.
    /// <para>
    /// A grant presented here is <i>validated and not consumed</i>. The register re-quotes on
    /// every keystroke; spending a single-use grant on the first of those would leave nothing
    /// for the sale it was minted for.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<SaleResponse>, ValidationProblem, ProblemHttpResult>> QuoteAsync(
        CreateSaleRequest request,
        [FromHeader(Name = OverrideGrantService.HeaderName)] string? overrideGrant,
        AppDbContext db,
        OverrideGrantService grants,
        System.Security.Claims.ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // The catalog prices are discarded here on purpose: a quote must not audit. The
        // register re-quotes on every keystroke, so writing a PriceOverridden entry from this
        // path would bury the log under a hundred rows per basket — the same reasoning that
        // keeps the quote from *consuming* an override grant.
        var (cart, errors, _, _) = await BuildCartAsync(db, request, quoting: true, cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var authorized = await AuthorizeAdjustmentsAsync(
            cart!,
            overrideGrant,
            grants,
            caller,
            authorization,
            registerId: null,
            cancellationToken);

        if (!authorized.Succeeded)
        {
            return OverrideRequired(authorized.Missing);
        }

        var priced = PricingEngine.Price(cart!);

        return TypedResults.Ok(Project(priced, Money.Zero));
    }

    /// <summary>
    /// Prices the cart and commits it, in one transaction, exactly once.
    /// </summary>
    private static async Task<Results<Created<SaleResponse>, ValidationProblem, ProblemHttpResult>> CreateAsync(
        CreateSaleRequest request,
        [FromHeader(Name = OverrideGrantService.HeaderName)] string? overrideGrant,
        AppDbContext db,
        ISaleWriter writer,
        IIdempotencyContext idempotency,
        IAuditLog audit,
        OverrideGrantService grants,
        PosMetrics metrics,
        TimeProvider timeProvider,
        System.Security.Claims.ClaimsPrincipal caller,
        IAuthorizationService authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(timeProvider);

        /*
         * Checked first, before the cart is even read.
         *
         * Everything below this — the catalog reads, the authorisation, the pricing — is work
         * done on behalf of a request that is going to be refused, and the refusal does not
         * depend on any of it. Asking here also means the answer is the same whether the
         * queued sale's products still exist, which matters: a till whose clock is wrong
         * should be told its clock is wrong, not told about a product that was deactivated
         * while it was offline.
         */
        if (request.OccurredAt is { } occurredAt)
        {
            var now = timeProvider.GetUtcNow();

            if (!OfflineSaleRules.IsAcceptableOccurredAt(occurredAt, now))
            {
                return OfflineSaleTimestampInvalid(occurredAt, now);
            }
        }

        var (cart, errors, tracked, catalogPrices) =
            await BuildCartAsync(db, request, quoting: false, cancellationToken);

        // A tip is cash the drawer has to account for, so it obeys the same storable-amount
        // rule every other amount does. Negative is refused rather than clamped: a negative tip
        // is a keying slip that would take money out of the drawer with nothing explaining it.
        if (request.Tip is { } tip && (tip < 0m || !Core.Catalog.CatalogRules.IsStorableAmount(tip)))
        {
            errors["tip"] = ["A tip of 0 or more with at most 4 decimal places is required."];
        }

        // Discounts and overrides are separately gated, and a Cashier sending one is refused
        // rather than silently ignored: unlike an unreadable costPrice, this changes what the
        // customer pays, so quietly dropping it would take the shop's money instead of theirs.
        // A manager's grant satisfies the policy without the cashier holding it — see
        // AuthorizeAdjustmentsAsync.
        var authorized = cart is null
            ? Authorization.Granted
            : await AuthorizeAdjustmentsAsync(
                cart,
                overrideGrant,
                grants,
                caller,
                authorization,
                request.RegisterId,
                cancellationToken);

        if (!authorized.Succeeded)
        {
            // Saved on its own, because there is no transaction to join — the work being
            // recorded is precisely that no work happened. Safe to save here because nothing
            // is tracked yet: BuildCartAsync reads AsNoTracking throughout, and the writer has
            // not been called. A refused attempt is exactly what an owner wants to see, and it
            // leaves no other trace anywhere.
            await audit.RecordStandaloneAsync(
                AuditAction.AuthorizationRefused,
                nameof(Sale),
                request.ClientTransactionId ?? Guid.Empty,
                after: new Dictionary<string, string?>
                {
                    ["attempted"] = string.Join(", ", authorized.Missing),
                    ["registerId"] = request.RegisterId?.ToString(),
                },
                cancellationToken: cancellationToken);

            return OverrideRequired(authorized.Missing);
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
                tracked,
                authorized.Grant?.UserId,
                request.OccurredAt,
                (Money)(request.Tip ?? 0m)),

            // Runs inside the writer's transaction, once the numbers are known. This is what
            // makes "the key is inserted in the same transaction as the work" true rather than
            // approximately true.
            committed =>
            {
                response = Project(priced, committed.ChangeGiven, committed, tenders, (Money)(request.Tip ?? 0m));

                // RecordedAt, not CompletedAt. The record describes *this attempt* — when the
                // server answered and what it answered with — and for a replayed offline sale
                // CompletedAt is hours earlier. Stamping the record with the trade's time
                // would age it against a clock it has nothing to do with.
                idempotency.Record(db, StatusCodes.Status201Created, response, committed.RecordedAt);

                // Same transaction, same reasoning as the idempotency record above: an entry
                // for a sale that rolled back would report money moving that never moved.
                RecordAdjustments(audit, priced, committed, catalogPrices, cart!, authorized);

                // Spent here and nowhere else. Consumed before this point, a sale that then
                // failed would leave the cashier needing the manager back for a second PIN;
                // consumed after the commit, a crash in between would leave it spendable again.
                if (authorized.Grant is { } grant)
                {
                    grants.Consume(grant, committed.SaleId);
                }
            },
            cancellationToken);

        // After the commit, deliberately: a discrepancy recorded by a transaction that then
        // rolled back never happened, and a counter cannot be decremented.
        //
        // Counted at all because a discrepancy is silent by design — the sale is never
        // blocked by one, the cashier is never told, and nothing else surfaces it until
        // somebody counts a shelf. A rise means the ledger and the shelves are drifting
        // apart, which is either theft, a receiving mistake, or a bug in the stock path.
        metrics.StockDiscrepanciesRecorded(result.Discrepancies.Count);

        return TypedResults.Created($"/api/v1/sales/{result.SaleId}", response!);
    }

    /// <summary>
    /// Records every price override and discount on a committed sale.
    /// </summary>
    /// <remarks>
    /// <b>Called from inside the writer's transaction</b>, so these entries commit with the
    /// sale or not at all.
    /// <para>
    /// The actor is always the session's own user — the cashier who rang it — even when a
    /// manager's grant is what permitted the exception. Collapsing the two would make "who did
    /// this" mean the cashier on some rows and the manager on others; the approver goes in the
    /// payload instead, where it can be read as what it is.
    /// </para>
    /// </remarks>
    private static void RecordAdjustments(
        IAuditLog audit,
        PricedSale priced,
        SaleCommitResult committed,
        IReadOnlyDictionary<Guid, Money> catalogPrices,
        Cart cart,
        Authorization authorized)
    {
        var approvedBy = authorized.Grant?.UserId.ToString();

        for (var index = 0; index < priced.Lines.Count; index++)
        {
            var line = priced.Lines[index].Source;
            var lineId = committed.LineIds[index];

            if (line.IsPriceOverridden && catalogPrices.TryGetValue(line.ProductId, out var wasPriced))
            {
                audit.Record(
                    AuditAction.PriceOverridden,
                    nameof(SaleLine),
                    lineId,
                    before: new Dictionary<string, string?>
                    {
                        ["unitPrice"] = Amount(wasPriced),
                    },
                    after: new Dictionary<string, string?>
                    {
                        ["unitPrice"] = Amount(line.UnitPrice),
                        ["saleId"] = committed.SaleId.ToString(),
                        ["productId"] = line.ProductId.ToString(),
                        ["approvedBy"] = approvedBy,
                    });
            }

            if (line.LineDiscount != Money.Zero)
            {
                audit.Record(
                    AuditAction.DiscountApplied,
                    nameof(SaleLine),
                    lineId,
                    after: new Dictionary<string, string?>
                    {
                        ["amount"] = Amount(line.LineDiscount),
                        ["saleId"] = committed.SaleId.ToString(),
                        ["productId"] = line.ProductId.ToString(),
                        ["approvedBy"] = approvedBy,
                    });
            }
        }

        // One more for a discount taken off the basket as a whole, which belongs to no line —
        // this is what the phase doc's "line or cart" means.
        if (cart.CartDiscount != Money.Zero)
        {
            audit.Record(
                AuditAction.DiscountApplied,
                nameof(Sale),
                committed.SaleId,
                after: new Dictionary<string, string?>
                {
                    ["amount"] = Amount(cart.CartDiscount),
                    ["scope"] = "cart",
                    ["approvedBy"] = approvedBy,
                });
        }
    }

    /// <summary>
    /// A money amount as the audit log stores it.
    /// </summary>
    /// <remarks>
    /// Invariant culture, explicitly. <c>InvariantGlobalization</c> is off, so a machine under
    /// a comma-decimal culture would otherwise write "1,20" into a permanent record that
    /// nothing can go back and correct.
    /// </remarks>
    private static string Amount(Money money) =>
        money.Amount.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Turns a request into a <see cref="Cart"/>, resolving every product against the catalog.
    /// </summary>
    /// <remarks>
    /// <b>One builder, both routes.</b> This is the mechanical reason a quote and the sale that
    /// follows it cannot disagree — not a promise that two code paths were kept in step. Two
    /// implementations of tax and discount rules will differ eventually, and the place it
    /// surfaces is a customer disputing a receipt at a counter.
    /// </remarks>
    private static async Task<(
        Cart? Cart,
        Dictionary<string, string[]> Errors,
        IReadOnlySet<Guid> Tracked,
        IReadOnlyDictionary<Guid, Money> CatalogPrices)>
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
            return (null, errors, new HashSet<Guid>(), new Dictionary<Guid, Money>());
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

        // Carried out of here purely so a price override can be audited against the price it
        // replaced. The CartLine below keeps only the price that was *used*, which is the
        // right shape for pricing and useless for "what did this cost before?" — and the
        // dictionary this comes from is a local that dies with the function.
        var catalogPrices = new Dictionary<Guid, Money>();

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

            catalogPrices[productId] = product.UnitPrice;

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
            return (null, errors, tracked, catalogPrices);
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
            tracked,
            catalogPrices);
    }

    private static async Task<Results<Ok<SaleResponse>, NotFound, ValidationProblem>> VoidAsync(
        Guid id,
        VoidSaleRequest request,
        AppDbContext db,
        ISaleWriter writer,
        IIdempotencyContext idempotency,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var reason = request.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            // Required. A void with no reason is exactly the record you need six months later
            // and will not have — the same rule as a stock adjustment's.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = ["A reason is required."],
            });
        }

        if (reason.Length > Sale.VoidReasonMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["reason"] = [$"A reason of at most {Sale.VoidReasonMaxLength} characters is required."],
            });
        }

        // 404 before the writer, so another tenant's sale is indistinguishable from one that
        // does not exist. The writer's locked re-read is what makes the decision safe.
        if (!await db.Sales.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        SaleResponse? response = null;

        await writer.VoidAsync(
            id,
            reason,
            voided =>
            {
                idempotency.Record(db, StatusCodes.Status200OK, VoidedMarker(voided), voided.VoidedAt);

                // A void hands the cash straight back and puts the goods on the shelf, so it
                // is the cheapest way to make a sale disappear. The reason is on the sale row
                // too; this is the entry an owner reads when looking for a pattern across
                // sales rather than at one.
                audit.Record(
                    AuditAction.SaleVoided,
                    nameof(Sale),
                    voided.SaleId,
                    before: new Dictionary<string, string?> { ["status"] = nameof(SaleStatus.Completed) },
                    after: new Dictionary<string, string?>
                    {
                        ["status"] = nameof(SaleStatus.Voided),
                        ["reason"] = reason,
                    });
            },
            cancellationToken);

        var sale = await db.Sales.AsNoTracking().FirstAsync(s => s.Id == id, cancellationToken);
        response = await ReadAsync(db, sale, cancellationToken);

        return TypedResults.Ok(response);
    }

    /// <summary>
    /// What a replayed void returns.
    /// </summary>
    /// <remarks>
    /// Deliberately not the whole sale. The stored body has to be written <i>inside</i> the
    /// transaction, before the sale can be read back in its final state, and inventing a
    /// snapshot there would risk it disagreeing with the row. A small, honest acknowledgement
    /// is better than a large one that might be wrong.
    /// </remarks>
    private static object VoidedMarker(SaleVoidResult voided) =>
        new { id = voided.SaleId, status = nameof(SaleStatus.Voided), voidedAt = voided.VoidedAt };

    private static async Task<Results<Created<SaleResponse>, NotFound, ValidationProblem>> RefundAsync(
        Guid id,
        RefundSaleRequest request,
        AppDbContext db,
        ISaleWriter writer,
        IIdempotencyContext idempotency,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new Dictionary<string, string[]>();

        var reason = request.Reason?.Trim();

        if (string.IsNullOrEmpty(reason))
        {
            errors["reason"] = ["A reason is required."];
        }

        if (request.ClientTransactionId is not { } clientTransactionId || clientTransactionId == Guid.Empty)
        {
            errors["clientTransactionId"] = ["A client transaction id is required."];
        }

        if (request.RegisterId is not { } registerId || registerId == Guid.Empty)
        {
            errors["registerId"] = ["A register is required."];
        }

        if (request.ShiftId is not { } shiftId || shiftId == Guid.Empty)
        {
            errors["shiftId"] = ["A shift is required."];
        }

        for (var index = 0; index < (request.Lines?.Count ?? 0); index++)
        {
            var line = request.Lines![index];

            if (line.SaleLineId is not { } lineId || lineId == Guid.Empty)
            {
                errors[$"lines[{index}].saleLineId"] = ["A sale line is required."];
            }

            if (line.Quantity is not { } quantity || quantity <= 0m)
            {
                errors[$"lines[{index}].quantity"] = ["A quantity greater than zero is required."];
            }
            else if (!Core.Catalog.CatalogRules.IsStorableAmount(quantity))
            {
                errors[$"lines[{index}].quantity"] = ["A quantity with at most 4 decimal places is required."];
            }
        }

        await ValidateShiftAsync(
            db,
            new CreateSaleRequest(null, request.RegisterId, request.ShiftId, null, null, null),
            errors,
            cancellationToken);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        if (!await db.Sales.AnyAsync(s => s.Id == id, cancellationToken))
        {
            return TypedResults.NotFound();
        }

        var increment = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == db.CurrentTenantId)
            .Select(t => t.CashRoundingIncrement)
            .FirstAsync(cancellationToken);

        SaleResponse? response = null;

        var result = await writer.RefundAsync(
            new SaleRefundRequest(
                id,
                request.ClientTransactionId!.Value,
                request.RegisterId!.Value,
                request.ShiftId!.Value,
                reason!,
                [.. (request.Lines ?? []).Select(l =>
                    new RefundLineInstruction(l.SaleLineId!.Value, l.Quantity!.Value))],
                increment),
            committed =>
            {
                idempotency.Record(
                    db,
                    StatusCodes.Status201Created,
                    new { id = committed.SaleId, saleNumber = committed.SaleNumber },
                    committed.CompletedAt);

                // Until now the entire trail for a refund was the reason and actor stored on
                // the refund row. This is the record that survives the row being read by
                // somebody who does not know to look for it, and it names both sales — the
                // question is always "how much has come back against this sale?".
                audit.Record(
                    AuditAction.RefundIssued,
                    nameof(Sale),
                    committed.SaleId,
                    after: new Dictionary<string, string?>
                    {
                        ["originalSaleId"] = id.ToString(),
                        ["saleNumber"] = committed.SaleNumber.ToString(CultureInfo.InvariantCulture),
                        ["reason"] = reason,
                    });
            },
            cancellationToken);

        var refund = await db.Sales.AsNoTracking().FirstAsync(s => s.Id == result.SaleId, cancellationToken);
        response = await ReadAsync(db, refund, cancellationToken);

        return TypedResults.Created($"/api/v1/sales/{result.SaleId}", response);
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

    /// <summary>Whether the caller may price this cart, and on whose authority.</summary>
    private sealed record Authorization(bool Succeeded, IReadOnlyList<string> Missing, OverrideGrant? Grant)
    {
        /// <summary>Nothing about this cart needed permission, or the caller held it.</summary>
        public static readonly Authorization Granted = new(true, [], null);
    }

    /// <summary>
    /// Checks the policies a cart's adjustments need, accepting a manager's grant in place of
    /// the caller's own role.
    /// </summary>
    /// <remarks>
    /// <b>One resolver for both the quote and the sale.</b> Two copies would eventually disagree
    /// about what a discount costs in permissions, and the way that surfaces is a total shown to
    /// a customer that the sale then refuses.
    /// <para>
    /// The caller's own policy is checked first, so a manager working the till never presents a
    /// grant to themselves. A grant is looked up under the tenant query filter with RLS beneath
    /// it, so one minted at another shop cannot resolve here whatever the token says.
    /// </para>
    /// </remarks>
    private static async Task<Authorization> AuthorizeAdjustmentsAsync(
        Cart cart,
        string? presentedGrant,
        OverrideGrantService grants,
        System.Security.Claims.ClaimsPrincipal caller,
        IAuthorizationService authorization,
        Guid? registerId,
        CancellationToken cancellationToken)
    {
        var required = new List<string>();

        if (NeedsDiscountPermission(cart) &&
            !(await authorization.AuthorizeAsync(caller, Policies.CanApplyDiscount)).Succeeded)
        {
            required.Add(Policies.CanApplyDiscount);
        }

        if (cart.Lines.Any(l => l.IsPriceOverridden) &&
            !(await authorization.AuthorizeAsync(caller, Policies.CanOverridePrice)).Succeeded)
        {
            required.Add(Policies.CanOverridePrice);
        }

        if (required.Count == 0)
        {
            return Authorization.Granted;
        }

        var resolution = await grants.ResolveAsync(presentedGrant, required, registerId, cancellationToken);

        return resolution.Grant is { } grant
            ? new Authorization(true, [], grant)
            : new Authorization(false, required, null);
    }

    /// <summary>
    /// The refusal a register can act on: "a manager has to authorise this", not a bare 403.
    /// </summary>
    /// <remarks>
    /// A stable <c>type</c> slug rather than the status code alone, per docs/API.md's global
    /// rules — 403 is shared with every other refusal in the API, and a till branching on it
    /// would offer a manager PIN for things no PIN can fix. The missing policies are listed so
    /// the dialog can ask for exactly those.
    /// </remarks>
    private static ProblemHttpResult OverrideRequired(IReadOnlyList<string> missing)
        => TypedResults.Problem(
            title: "Manager authorisation required",
            detail: "A discount or a price override on this sale needs a manager's authorisation.",
            statusCode: StatusCodes.Status403Forbidden,
            type: "https://pos.example/errors/override-required",
            extensions: new Dictionary<string, object?>
            {
                ["requiredPolicies"] = missing,
            });

    /// <summary>
    /// A sale whose <c>occurredAt</c> the server will not date a row by.
    /// </summary>
    /// <remarks>
    /// A typed problem rather than a field error, because the outbox has to classify this and
    /// classify it correctly: it is <b>permanent</b>. Retrying it changes nothing — the same
    /// body is sent under the same key, and the answer is the same — so a client that treated
    /// it as transient would back off for ever and the sale would never reach a person. As a
    /// <c>problem+json</c> type it lands in the review queue on the first attempt, which is
    /// where a sale nobody can date belongs.
    /// <para>
    /// 422 rather than 400: the request is well-formed and every field is the right shape. What
    /// is wrong is that the server does not believe the till's clock, and that is a semantic
    /// refusal about content — the distinction matters here because the client branches on the
    /// status before it reads the type.
    /// </para>
    /// </remarks>
    private static ProblemHttpResult OfflineSaleTimestampInvalid(
        DateTimeOffset occurredAt,
        DateTimeOffset now)
        => TypedResults.Problem(
            title: "That sale cannot be dated",
            detail: OfflineSaleRules.DescribeRefusal(occurredAt, now),
            statusCode: StatusCodes.Status422UnprocessableEntity,
            type: "https://pos.example/errors/offline-sale-timestamp-invalid",
            extensions: new Dictionary<string, object?>
            {
                ["occurredAt"] = occurredAt,
                ["serverTime"] = now,
            });

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
    /// <param name="tip">
    /// What was left for the staff. Carried through because this projection is built from the
    /// priced cart rather than from the stored row — the tip is not a priced amount, so it has
    /// nowhere else to come from, and without it the response a till renders its receipt from
    /// would say the customer tipped nothing.
    /// </param>
    private static SaleResponse Project(
        PricedSale priced,
        Money change,
        SaleCommitResult? committed = null,
        IReadOnlyList<TenderInstruction>? tenders = null,
        Money tip = default) =>
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
                index == 0 && !change.IsZero ? (decimal)change : null))],
            RecordedAt: committed?.RecordedAt,
            TipAmount: (decimal)tip);

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

        /*
         * The refunds written against this sale — the direction that is easy to leave out.
         *
         * A refund knows its original through OriginalSaleId. Without the reverse, somebody
         * looking at a sale a customer has brought back cannot see it was already refunded, and
         * refunds it a second time. Voided refunds are excluded: they reversed nothing.
         */
        var refunds = await db.Sales
            .AsNoTracking()
            .Where(s => s.OriginalSaleId == sale.Id && s.Status != SaleStatus.Voided)
            .OrderBy(s => s.SaleNumber)
            .Select(s => new LinkedRefundResponse(
                s.Id, s.SaleNumber, (decimal)s.Total, s.CompletedAt))
            .ToListAsync(cancellationToken);

        var originalSaleNumber = sale.OriginalSaleId is { } originalId
            ? await db.Sales
                .AsNoTracking()
                .Where(s => s.Id == originalId)
                .Select(s => (long?)s.SaleNumber)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

        // Current values, like a receipt's. Invariant 5 is about amounts; a renamed cashier is
        // the same person, and a history naming who they used to be helps nobody.
        var names = await db.Users
            .AsNoTracking()
            .Where(u => u.Id == sale.CashierId)
            .Select(u => u.DisplayName)
            .FirstOrDefaultAsync(cancellationToken);

        var registerName = await db.Registers
            .AsNoTracking()
            .Where(r => r.Id == sale.RegisterId)
            .Select(r => r.Name)
            .FirstOrDefaultAsync(cancellationToken);

        var voidedByName = sale.VoidedBy is { } voidedBy
            ? await db.Users
                .AsNoTracking()
                .Where(u => u.Id == voidedBy)
                .Select(u => u.DisplayName)
                .FirstOrDefaultAsync(cancellationToken)
            : null;

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
            tenders,
            sale.RegisterId,
            sale.ShiftId,
            sale.CashierId,
            names ?? "Unknown",
            registerName ?? "Unknown",
            sale.TaxMode,
            sale.OriginalSaleId,
            originalSaleNumber,
            sale.VoidedAt,
            voidedByName,
            sale.VoidReason,
            sale.RefundReason,
            refunds,
            sale.RecordedAt,
            (decimal)sale.TipAmount);
    }
}
