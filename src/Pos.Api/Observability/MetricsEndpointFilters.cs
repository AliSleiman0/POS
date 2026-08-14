using System.Diagnostics;
using Pos.Api.Idempotency;

namespace Pos.Api.Observability;

/// <summary>
/// Records the outcome of a sale submission, however it ended.
/// </summary>
/// <remarks>
/// A filter rather than lines inside the handler. <c>CreateAsync</c> has a dozen exit
/// paths — validation, a closed shift, an unauthorised discount, a concurrent stock
/// update, an idempotent replay — and instrumenting a handler with a dozen returns means
/// eleven of them get it right. This sees the status the caller actually received,
/// including a 500 raised by something nobody anticipated, which is the case that matters
/// most and the one a hand-placed counter always misses.
/// </remarks>
public sealed class SaleSubmissionMetricsFilter(PosMetrics metrics) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        try
        {
            var result = await next(context);

            Record(context.HttpContext, result);

            return result;
        }
        catch
        {
            // An exception that escapes has not set a status yet — the exception handler
            // will, after this frame has unwound. 500 is what it becomes, and counting it
            // here is the difference between "sale submissions are failing" and silence.
            metrics.SaleSubmitted(StatusCodes.Status500InternalServerError, replayed: false);
            throw;
        }
    }

    private void Record(HttpContext httpContext, object? result)
    {
        // The header the idempotency filter sets when it returns a stored response. A
        // replay is a success: the money was taken on the first attempt, and counting it
        // as anything else would make the failure rate a fiction precisely during the
        // network trouble that produced the retry.
        var replayed = httpContext.Response.Headers
            .TryGetValue(IdempotencyFilter.ReplayHeaderName, out var value)
            && string.Equals(value.ToString(), "true", StringComparison.OrdinalIgnoreCase);

        metrics.SaleSubmitted(StatusOf(result) ?? httpContext.Response.StatusCode, replayed);
    }

    /// <summary>
    /// The status the caller will receive, read from the returned result.
    /// </summary>
    /// <remarks>
    /// <b>Not <c>HttpContext.Response.StatusCode</c>.</b> A minimal API handler returns an
    /// <c>IResult</c> and nothing writes a status until that result is <i>executed</i>,
    /// which happens after every filter has returned. Reading the response here sees the
    /// untouched default of 200 — so every refused sale would be counted as completed and
    /// the failure rate would be flat by construction. Found by
    /// <c>MetricsTests.A_refused_sale_is_counted_as_refused_and_not_as_an_error</c>, which
    /// is the entire reason that test exists.
    /// <para>
    /// The response is still the fallback, for a result type that reports no status of its
    /// own.
    /// </para>
    /// </remarks>
    private static int? StatusOf(object? result) => result switch
    {
        // Results<T1, T2, …> — the union every handler here declares — wraps the result
        // that was actually chosen.
        INestedHttpResult nested => StatusOf(nested.Result),
        IStatusCodeHttpResult { StatusCode: { } status } => status,
        _ => null,
    };
}

/// <summary>
/// Times a barcode lookup — the scan-to-line delay a cashier experiences as "slow".
/// </summary>
/// <remarks>
/// Measured here rather than from the client because this is the part we can fix. Phase
/// 2.3 made the lookup answer in one statement and asserted that on the generated SQL;
/// this is what would notice if it ever stopped being true, which a correctness test
/// cannot — the wrong answer arrives, just late.
/// </remarks>
public sealed class BarcodeLookupMetricsFilter(PosMetrics metrics) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);

        var started = Stopwatch.GetTimestamp();

        try
        {
            return await next(context);
        }
        finally
        {
            // In the finally, so a lookup that threw still contributes its latency. A
            // percentile computed only over successes flatters exactly the failure mode
            // worth seeing: a timeout.
            metrics.BarcodeLookedUp(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
