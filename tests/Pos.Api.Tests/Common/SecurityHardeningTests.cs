using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;

namespace Pos.Api.Tests.Common;

/// <summary>
/// The rate limits and headers from the Phase 8.6 hardening pass.
/// </summary>
/// <remarks>
/// Both are the kind of protection that is easy to configure and easy to configure into a
/// state where it does nothing: a policy registered but never attached to a route, a header
/// set after the response has begun and silently dropped. Neither mistake shows up anywhere
/// except in a test that asks.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SecurityHardeningTests(PosApiFactory factory)
{
    /// <summary>
    /// A client with a rate-limit partition of its own.
    /// </summary>
    /// <remarks>
    /// Every request through WebApplicationFactory arrives with no remote address, so
    /// without this the whole assembly shares one partition and a test that deliberately
    /// exhausts the login limit makes every later test fail at login. Which is exactly the
    /// collision a shop behind NAT has — see LoginAttemptsPerWindow.
    /// </remarks>
    private HttpClient IsolatedClient()
    {
        var address = ClientAddressStartupFilter.Unique();

        return factory
            .WithWebHostBuilder(builder => builder.ConfigureServices(
                services => ClientAddressStartupFilter.Register(services, address)))
            .CreateClient();
    }

    [Fact]
    public async Task Hammering_login_is_refused_before_the_password_is_even_checked()
    {
        using var client = IsolatedClient();

        var attempts = RateLimitingServiceCollectionExtensions.LoginAttemptsPerWindow + 5;
        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { tenantSlug = "iso-b", email = $"guess{attempt}@nowhere.test", password = "wrong" });

            statuses.Add(response.StatusCode);
        }

        // Identity's per-account lockout would never fire here — every attempt names a
        // different address, which is exactly the attack it cannot see. This is the cap
        // that does.
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task A_rate_limited_caller_is_told_how_long_to_wait()
    {
        using var client = IsolatedClient();

        HttpResponseMessage? limited = null;

        for (var attempt = 0; attempt < 40 && limited is null; attempt++)
        {
            var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { tenantSlug = "iso-b", email = "someone@nowhere.test", password = "wrong" });

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                limited = response;
            }
            else
            {
                response.Dispose();
            }
        }

        Assert.NotNull(limited);

        using (limited)
        {
            // Without it a shop's whole staff retries in a tight loop at nine o'clock and
            // keeps the window permanently exhausted for the one person typing correctly.
            Assert.True(
                limited.Headers.RetryAfter is not null,
                "A 429 with no Retry-After tells a client to guess, and clients guess badly.");
        }
    }

    [Fact]
    public async Task Ordinary_traffic_is_not_rate_limited_at_all()
    {
        using var client = IsolatedClient();

        // There is deliberately no application-wide limiter — see AddPosRateLimiting. The
        // two limits that exist are aimed at guessing a credential; a blanket cap would
        // have to sit above whatever the busiest real shop does at its busiest minute, a
        // figure nobody has measured, and every estimate that turns out low takes a till
        // down mid-queue. Volumetric protection is the edge's job (fly.toml concurrency).
        //
        // This is what would notice a global limiter being added back without that
        // argument being revisited.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            using var response = await client.GetAsync("/health/live");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Theory]
    [InlineData("X-Content-Type-Options", "nosniff")]
    [InlineData("X-Frame-Options", "DENY")]
    [InlineData("Referrer-Policy", "no-referrer")]
    public async Task Every_response_carries_the_security_headers(string header, string expected)
    {
        using var client = IsolatedClient();

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(expected, Assert.Single(response.Headers.GetValues(header)));
    }

    [Fact]
    public async Task An_error_response_carries_them_too()
    {
        var world = await factory.IsolationWorldAsync();
        using var client = IsolatedClient();

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        // A deliberate 500. The error response is the one most likely to carry a message a
        // browser should not be guessing the content type of, so the headers matter most
        // here — and setting them after the response has begun drops them silently.
        using var response = await client.PostAsync("/api/v1/diagnostics/test-error", content: null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
    }

    [Fact]
    public async Task Hsts_is_not_sent_outside_production()
    {
        using var client = IsolatedClient();

        using var response = await client.GetAsync("/health/live");

        // The test host runs as "Testing". Sending HSTS from a non-production host pins a
        // max-age into a developer's browser for plain-HTTP localhost, which is remarkably
        // annoying to undo and catches the next person rather than the one who caused it.
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}
