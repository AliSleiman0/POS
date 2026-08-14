using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Pos.Api.Observability;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;

namespace Pos.Api.Tests.Common;

/// <summary>
/// The instruments actually record.
/// </summary>
/// <remarks>
/// Worth testing for a reason peculiar to metrics: <b>an instrument nobody writes to and a
/// system with nothing wrong look exactly the same on a dashboard.</b> A counter wired to
/// the wrong branch, or a filter attached to no route, produces a flat line — which is what
/// success looks like — and the mistake is discovered during the incident the alert was
/// supposed to catch.
/// <para>
/// <see cref="MetricCollector{T}"/> subscribes to the real meter in the running host, so
/// this asserts on the same path a scraper would read rather than on a mock.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class MetricsTests(PosApiFactory factory)
{
    [Fact]
    public async Task A_refused_sale_is_counted_as_refused_and_not_as_an_error()
    {
        using var client = factory.CreateClient();
        var world = await factory.IsolationWorldAsync();

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.CashierEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            PosMetrics.MeterName,
            "pos.sales.submissions");

        // A cart with no lines: refused with a validation problem, which is the system
        // telling a cashier something true.
        using var response = await client.PostIdempotentAsync(
            "/api/v1/sales",
            new { clientTransactionId = Guid.CreateVersion7(), lines = Array.Empty<object>() });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());

        Assert.Equal(1, measurement.Value);

        // "refused", not "error". This is the distinction the whole instrument exists for:
        // alerting on 4xx would page somebody because a customer did not have enough cash,
        // and an alert that fires for that gets muted — after which it is worse than
        // nothing, because everybody believes it is still watching.
        Assert.Equal("refused", measurement.Tags["outcome"]);
    }

    [Fact]
    public async Task A_replayed_submission_is_counted_and_counted_as_a_replay()
    {
        // A real committed sale, because only a committed one is stored for replay: a
        // refused submission writes no idempotency record, so re-sending it re-runs the
        // validation rather than replaying anything.
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();

        var body = new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 10m } },
        };

        using var first = await client.PostIdempotentAsync("/api/v1/sales", body, key);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);

        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            PosMetrics.MeterName,
            "pos.sales.submissions");

        // Same key, same body: answered from the stored response without re-charging.
        using var second = await client.PostIdempotentAsync("/api/v1/sales", body, key);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());

        // Counted at all is what the filter ordering buys. The metrics filter is registered
        // BEFORE RequireIdempotency so it sits outside it; registered after, the idempotency
        // filter would short-circuit the replay without ever calling in, and every retried
        // sale would vanish from the numbers during precisely the network trouble that
        // produced the retry.
        Assert.Equal(1, measurement.Value);

        // And counted as its own outcome. A replay is a success — the money was taken on
        // the first attempt — so folding it into either other bucket makes the failure rate
        // a fiction exactly when somebody is looking at it.
        Assert.Equal("replayed", measurement.Tags["outcome"]);
    }

    [Fact]
    public async Task A_barcode_lookup_is_timed()
    {
        using var client = factory.CreateClient();
        var world = await factory.IsolationWorldAsync();

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.CashierEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        using var collector = new MetricCollector<double>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            PosMetrics.MeterName,
            "pos.barcode.lookup.duration");

        // A code that resolves to nothing. Timed anyway — a percentile computed only over
        // successful lookups flatters exactly the failure mode worth seeing.
        using var response = await client.GetAsync("/api/v1/products/by-barcode/not-a-real-code");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var measurement = Assert.Single(collector.GetMeasurementSnapshot());

        Assert.True(
            measurement.Value >= 0,
            "A lookup was timed at a negative duration, which means the stopwatch is being read wrongly.");
    }

    [Fact]
    public async Task A_rejected_refresh_token_is_counted()
    {
        using var client = factory.CreateClient();

        using var collector = new MetricCollector<long>(
            factory.Services.GetRequiredService<IMeterFactory>(),
            PosMetrics.MeterName,
            "pos.auth.refresh_failures");

        using var response = await client.PostAsJsonAsync(
            "/api/v1/auth/refresh",
            new { refreshToken = "not-a-token-anybody-issued" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        Assert.Equal(1, Assert.Single(collector.GetMeasurementSnapshot()).Value);
    }

    [Fact]
    public void The_meter_name_is_the_one_a_scraper_is_configured_with()
    {
        // Pinned because renaming it is a silent break: every dashboard and alert built on
        // the old name keeps rendering, empty, and an empty chart is indistinguishable from
        // a healthy one.
        Assert.Equal("Pos.Api", PosMetrics.MeterName);
    }
}
