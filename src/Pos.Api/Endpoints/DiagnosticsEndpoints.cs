using Microsoft.AspNetCore.Http.HttpResults;
using Pos.Api.Auth;

namespace Pos.Api.Endpoints;

/// <summary>
/// Raised by <c>POST /diagnostics/test-error</c>. Nothing catches it on purpose.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> a <c>PosDomainException</c>. Those are mapped to
/// <c>problem+json</c> by <c>DomainExceptionHandler</c> and logged at Warning, because they
/// are the system working as designed — which is exactly the path this must not take. The
/// point is to exercise what happens to an <i>unhandled</i> exception: the 500 a client
/// sees, the stack trace that must not be in it, and the report that must reach error
/// tracking carrying the tenant.
/// </remarks>
public sealed class DiagnosticsTestException(string message) : Exception(message);

/// <summary>
/// One endpoint, whose only job is to prove that error tracking still works.
/// </summary>
/// <remarks>
/// "Is Sentry still receiving?" is a question with two bad answers and one good one. The
/// bad ones are assuming yes — a DSN rotated six months ago fails silently and the first
/// evidence is an incident nobody was paged for — and finding out by waiting for a real
/// defect. This is the good one.
/// <para>
/// <b>Why it is authenticated.</b> An anonymous <c>/debug/boom</c> is a free way for
/// anybody to fill a shop's error quota and bury a real report under noise, and every
/// endpoint in this application states a policy because a test fails the build on one that
/// does not. Owner-only via <c>CanManageEmployees</c> rather than a policy of its own: this
/// is an operational control on the level of resetting somebody's PIN, and adding a policy
/// nothing else uses would put a row in the matrix for one route.
/// </para>
/// </remarks>
public static class DiagnosticsEndpoints
{
    public const string TestErrorMessage =
        "Deliberate test error from POST /api/v1/diagnostics/test-error. " +
        "If you are reading this in error tracking, it is working.";

    public static IEndpointRouteBuilder MapDiagnosticsEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var diagnostics = builder.MapGroup("/api/v1/diagnostics").WithTags("Diagnostics");

        diagnostics.MapPost("/test-error", TestErrorAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Raise a deliberate unhandled error, to prove error tracking receives it");

        return diagnostics as IEndpointRouteBuilder ?? builder;
    }

    /// <summary>
    /// Throws. The declared return type exists only so OpenAPI describes the operation.
    /// </summary>
    /// <remarks>
    /// A bare <c>IResult</c> would produce an operation with no response content, which the
    /// generated TypeScript client types as <c>never</c> — the contract requirement pinned
    /// by <c>ResponseSchemaContractTests</c>. Nothing ever reaches the success branch.
    /// </remarks>
    private static Task<Results<NoContent, ProblemHttpResult>> TestErrorAsync() =>
        throw new DiagnosticsTestException(TestErrorMessage);
}
