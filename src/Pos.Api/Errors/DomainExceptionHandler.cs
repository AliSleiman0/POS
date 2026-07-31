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

        _ => (StatusCodes.Status400BadRequest, "Request could not be completed"),
    };
}
