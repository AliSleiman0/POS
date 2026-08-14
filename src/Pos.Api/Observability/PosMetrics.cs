using System.Diagnostics.Metrics;

namespace Pos.Api.Observability;

/// <summary>
/// The handful of numbers worth waking somebody up for.
/// </summary>
/// <remarks>
/// Deliberately four instruments and not forty. A dashboard nobody reads is not
/// observability, and an alert that fires weekly is an alert that gets muted — after which
/// it is worse than nothing, because everyone believes it is still watching.
/// <para>
/// Each of these answers a question a shop would otherwise ask us by telephone, and each
/// is a leading indicator rather than a symptom:
/// </para>
/// <list type="bullet">
/// <item><b>Sale submissions failing</b> — the till cannot take money. Nothing else on
/// this list matters if this one is firing.</item>
/// <item><b>Barcode lookup latency</b> — the scan-to-line delay is what a cashier
/// experiences as "the system is slow", and it is the first thing to degrade as a
/// catalog grows.</item>
/// <item><b>Refresh-token failures</b> — a spike means either staff being signed out
/// mid-shift or a token family being replayed, and the two need different responses.</item>
/// <item><b>Stock discrepancies created</b> — the ledger disagreeing with itself.
/// Silent by design (a sale is never blocked by one), so nothing else surfaces it.</item>
/// </list>
/// <para>
/// <b>No tenant tag.</b> Metrics are cardinality-sensitive: one time series per tenant per
/// instrument grows with the customer list and is what turns a metrics bill into a
/// surprise. Tenant-level attribution is the logs' job, where <c>TenantId</c> is on every
/// line — see <c>TenantResolutionMiddleware</c>. Metrics say "something is wrong"; logs
/// say "for whom".
/// </para>
/// </remarks>
public sealed class PosMetrics : IDisposable
{
    /// <summary>
    /// The meter name to configure in a scraper. Stable — renaming it silently empties
    /// every dashboard and alert built on it.
    /// </summary>
    public const string MeterName = "Pos.Api";

    private readonly Meter _meter;

    private readonly Counter<long> _saleSubmissions;
    private readonly Histogram<double> _barcodeLookupDuration;
    private readonly Counter<long> _refreshFailures;
    private readonly Counter<long> _stockDiscrepancies;

    public PosMetrics(IMeterFactory meterFactory)
    {
        ArgumentNullException.ThrowIfNull(meterFactory);

        _meter = meterFactory.Create(MeterName);

        // A counter of every attempt tagged by outcome, not a counter of failures. A raw
        // failure count cannot be alerted on: five failures is a catastrophe for a corner
        // shop on a Tuesday morning and a rounding error for a busy one on a Saturday.
        // A rate needs the denominator.
        _saleSubmissions = _meter.CreateCounter<long>(
            "pos.sales.submissions",
            unit: "{submission}",
            description: "Sale submissions, tagged by outcome and status code.");

        _barcodeLookupDuration = _meter.CreateHistogram<double>(
            "pos.barcode.lookup.duration",
            unit: "ms",
            description: "How long a barcode lookup took, which is the scan-to-line delay a cashier feels.");

        _refreshFailures = _meter.CreateCounter<long>(
            "pos.auth.refresh_failures",
            unit: "{failure}",
            description: "Refresh tokens rejected. A spike is either mass sign-out or a replayed family.");

        _stockDiscrepancies = _meter.CreateCounter<long>(
            "pos.stock.discrepancies",
            unit: "{discrepancy}",
            description: "Stock discrepancies recorded by a sale. Never blocks the sale, so nothing else surfaces it.");
    }

    /// <summary>One sale submission finished, however it finished.</summary>
    /// <param name="statusCode">The HTTP status the caller received.</param>
    /// <param name="replayed">
    /// True when the idempotency filter returned a stored response. Counted separately
    /// because a replay is a success — the money was taken on the first attempt — and
    /// folding it into either bucket makes the failure rate a fiction.
    /// </param>
    public void SaleSubmitted(int statusCode, bool replayed) =>
        _saleSubmissions.Add(
            1,
            new KeyValuePair<string, object?>("outcome", Outcome(statusCode, replayed)),
            new KeyValuePair<string, object?>("status", statusCode));

    public void BarcodeLookedUp(double milliseconds) =>
        _barcodeLookupDuration.Record(milliseconds);

    /// <summary>A refresh token was rejected.</summary>
    public void RefreshRejected() => _refreshFailures.Add(1);

    /// <summary>A sale recorded <paramref name="count"/> stock discrepancies.</summary>
    public void StockDiscrepanciesRecorded(int count)
    {
        if (count > 0)
        {
            _stockDiscrepancies.Add(count);
        }
    }

    /// <summary>
    /// Three buckets, not two.
    /// </summary>
    /// <remarks>
    /// A 4xx here is overwhelmingly the cashier being told something true — under-tender,
    /// a closed shift, a discount they may not apply. Alerting on it would page somebody
    /// because a customer did not have enough cash. A 5xx is the software failing to take
    /// money, which is the thing worth waking up for.
    /// </remarks>
    private static string Outcome(int statusCode, bool replayed) => (statusCode, replayed) switch
    {
        (_, true) => "replayed",
        (>= 500, _) => "error",
        (>= 400, _) => "refused",
        _ => "completed",
    };

    public void Dispose() => _meter.Dispose();
}
