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

        // Also 409, and for the same reason: two writers raced and one lost. Retrying after
        // a re-read is a meaningful thing to do, which is what separates this from a 400.
        DefaultTaxClassConflictException => (StatusCodes.Status409Conflict, "Default tax class changed concurrently"),

        // 400, and stated rather than left to the fallback below, because the fallback's
        // title ("Request could not be completed") tells a client nothing about which field
        // to blame. No race is involved: that parent can never be that category's parent.
        CategoryCycleException => (StatusCodes.Status400BadRequest, "Category hierarchy would form a cycle"),

        _ => (StatusCodes.Status400BadRequest, "Request could not be completed"),
    };
}
