using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Pos.Core.Exceptions;

namespace Pos.Api.Errors;

/// <summary>
/// Turns domain exceptions into RFC 9457 <c>application/problem+json</c>, in one place.
/// </summary>
/// <remarks>
/// One place, because the alternative is every endpoint growing its own try/catch and the
/// error contract drifting per route. Clients branch on <c>type</c>, which is a stable
/// slug; <c>detail</c> is human-facing prose and may be reworded at any time.
/// <para>
/// Nothing from the exception's stack or inner exceptions reaches the client. What a
/// server got wrong is a server's business.
/// </para>
/// </remarks>
public sealed partial class DomainExceptionHandler(
    IProblemDetailsService problemDetails,
    ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    [LoggerMessage(
        EventId = 1000,
        Level = LogLevel.Warning,
        Message = "Domain exception {ErrorType} on {Path}")]
    private static partial void LogDomainException(
        ILogger logger,
        string errorType,
        PathString path,
        Exception exception);

    /// <summary>Base for the stable <c>type</c> URIs. Documented in docs/API.md.</summary>
    private const string ErrorTypeBase = "https://pos.example/errors/";

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (exception is not PosDomainException domainException)
        {
            return false;
        }

        var (status, title) = Describe(domainException);

        // Logged at Warning, not Error: these are the system working as designed. A
        // cross-tenant write attempt is still worth seeing in a log, though.
        LogDomainException(logger, domainException.ErrorType, httpContext.Request.Path, domainException);

        httpContext.Response.StatusCode = status;

        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = domainException,
            ProblemDetails = new ProblemDetails
            {
                Type = ErrorTypeBase + domainException.ErrorType,
                Title = title,
                Status = status,
                Detail = domainException.Message,
                Instance = httpContext.Request.Path,
            },
        });
    }

    private static (int Status, string Title) Describe(PosDomainException exception) => exception switch
    {
        // 403 rather than 404: the caller reached a resource inside their own tenant and
        // then tried to write outside it. There is nothing to conceal about that, and a
        // 404 would send them looking for a bug that is theirs.
        CrossTenantWriteException => (StatusCodes.Status403Forbidden, "Cross-tenant write refused"),

        // No tenant on a request that needs one is a bug on our side, not the caller's.
        TenantNotResolvedException => (StatusCodes.Status500InternalServerError, "Tenant could not be resolved"),

        // 409, not 400: the body is well-formed and would be accepted tomorrow if the other
        // product were renamed. Nothing about the request itself is wrong, so there is no
        // field to point an `errors` map at — the client shows `detail` against the SKU
        // input and branches on `type`.
        DuplicateSkuException => (StatusCodes.Status409Conflict, "SKU already in use"),

        // The same shape one level down: a code already on some product in this tenant. The
        // title deliberately does not name which product — see the remarks on the exception.
        DuplicateBarcodeException => (StatusCodes.Status409Conflict, "Barcode already in use"),

        // Also 409, and for the same reason: two writers raced and one lost. Retrying after
        // a re-read is a meaningful thing to do, which is what separates this from a 400.
        DefaultTaxClassConflictException => (StatusCodes.Status409Conflict, "Default tax class changed concurrently"),

        // The same race one table over, detected by StockItem's xmin token. Deliberately not
        // retried for the caller — see the remarks on the exception.
        ConcurrentStockUpdateException => (StatusCodes.Status409Conflict, "Stock changed concurrently"),

        // 400, and stated rather than left to the fallback below, because the fallback's
        // title ("Request could not be completed") tells a client nothing about which field
        // to blame. No race is involved: that parent can never be that category's parent.
        CategoryCycleException => (StatusCodes.Status400BadRequest, "Category hierarchy would form a cycle"),

        // 400 and named, for the same reason as the row above: the fallback's title says
        // nothing about which field to blame. Nothing raced — €12 off a €10 basket is a
        // statement about the body and would be equally wrong tomorrow.
        InvalidDiscountException => (StatusCodes.Status400BadRequest, "Discount is not valid"),

        // 409 rather than 400, on the same reasoning as the duplicate-key rows above: the
        // body is well-formed and would be accepted unchanged with one more note on the
        // counter. There is no field to blame, so there is no errors map to fill.
        UnderTenderException => (StatusCodes.Status409Conflict, "Tender does not cover the sale"),

        // 409, and loudly. The alternative — replaying the stored response — would show the
        // till a sale that succeeded, for a basket the customer never had.
        IdempotencyKeyReusedException => (StatusCodes.Status409Conflict, "Idempotency key already used"),

        // 409 rather than a field error, because the caller's next move is an action rather
        // than a correction: open a shift. An *unknown* shift id is the field error, and the
        // endpoint answers that one 400 before ever reaching the writer.
        ShiftClosedException => (StatusCodes.Status409Conflict, "Shift is closed"),

        // Also 409: two tills raced to open one drawer and this one lost the filtered unique
        // index. Re-reading is meaningful — GET /shifts/current returns the winner.
        ShiftAlreadyOpenException => (StatusCodes.Status409Conflict, "Register already has an open shift"),

        // 409, not 404: the sale exists and the caller can see it. What changed is that
        // somebody voided it already, and re-voiding would put the goods back twice.
        SaleAlreadyVoidedException => (StatusCodes.Status409Conflict, "Sale is not voidable"),

        // Also 409, and the caller has a real next move — refund the remainder, or void the
        // refund first.
        SaleAlreadyRefundedException => (StatusCodes.Status409Conflict, "Sale has already been refunded"),

        // 409 rather than a field error: the quantity was well-formed and would have been
        // accepted a moment earlier. What it collides with is another refund, which is a race
        // the caller resolves by re-reading what remains.
        RefundExceedsOriginalException => (StatusCodes.Status409Conflict, "Refund exceeds what remains"),

        // The three lock-out guards, all 409 on the same reasoning as the rows above: the
        // body is well-formed and names real things, and would be accepted if the shop had
        // one more owner in it. Nothing about the request is wrong, so there is no field to
        // blame — what the caller needs is another person, not a corrected value.
        SelfDemotionException => (StatusCodes.Status409Conflict, "An owner cannot demote themselves"),

        SelfDeactivationException => (StatusCodes.Status409Conflict, "You cannot deactivate yourself"),

        LastOwnerException => (StatusCodes.Status409Conflict, "The shop must keep one active owner"),

        // Also 409, and the one on this list with no next move at all: once a shop has traded,
        // its tax mode is fixed for ever. Stated plainly rather than dressed up as a temporary
        // obstacle, because pretending otherwise sends somebody looking for the override.
        TaxModeLockedException => (StatusCodes.Status409Conflict, "Tax mode is fixed once trading starts"),

        // 409 and not 403: the caller is not forbidden — an owner holds every policy there is —
        // and the route exists, so a 404 would send a client hunting for a typo it will not
        // find. What is wrong is the shop's state, and it is one the caller can change.
        RestaurantModeRequiredException => (StatusCodes.Status409Conflict, "Restaurant mode is not turned on"),

        // Two staff raced to seat one table and this one lost the filtered unique index —
        // ShiftAlreadyOpenException's situation exactly. Re-reading is meaningful: GET /orders
        // returns the order that is already on the table.
        TableAlreadyOccupiedException => (StatusCodes.Status409Conflict, "Table already has an open order"),

        // 409 on ShiftClosedException's reasoning: the body is well-formed and names real
        // things, and would have been accepted a moment earlier. What changed is that somebody
        // settled or abandoned the order, and the caller's move is to look at what it became.
        OrderNotOpenException => (StatusCodes.Status409Conflict, "Order is no longer open"),

        // 409 with a real next move, unlike a locked tax mode: settle or abandon the tables and
        // the same request succeeds.
        OrdersStillOpenException => (StatusCodes.Status409Conflict, "Orders are still open"),

        _ => (StatusCodes.Status400BadRequest, "Request could not be completed"),
    };
}
