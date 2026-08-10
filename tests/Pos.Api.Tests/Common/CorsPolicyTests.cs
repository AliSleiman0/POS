using System.Reflection;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Idempotency;
using Pos.Api.Tests.Infrastructure;
using CorsOptions = Pos.Api.Auth.CorsOptions;

namespace Pos.Api.Tests.Common;

/// <summary>
/// Which browser origins may call this API, and which headers survive the crossing.
/// </summary>
/// <remarks>
/// CORS is enforced entirely by the browser, so every mistake here is invisible from the
/// server: the API keeps answering, keeps passing health checks, and the only symptom is a
/// console message on a device in a shop. These tests are the server-side substitute for
/// the browser that is not present.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CorsPolicyTests(PosApiFactory factory)
{
    private const string Allowed = "https://pos.fly.dev";
    private const string Attacker = "https://not-the-shop.example";

    /// <summary>Anonymous and mapped everywhere, so this is about the pipeline only.</summary>
    private const string AnonymousPath = "/health/live";

    private HttpClient CreateClient(params string[] origins)
    {
        var configured = factory.WithWebHostBuilder(builder =>
        {
            for (var i = 0; i < origins.Length; i++)
            {
                builder.UseSetting($"{CorsOptions.Keys.AllowedOrigins}:{i}", origins[i]);
            }
        });

        return configured.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task An_allowed_origin_is_echoed_back()
    {
        using var client = CreateClient(Allowed);

        using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousPath);
        request.Headers.Add("Origin", Allowed);

        using var response = await client.SendAsync(request);

        Assert.Equal(Allowed, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task An_unlisted_origin_gets_no_allow_header()
    {
        using var client = CreateClient(Allowed);

        using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousPath);
        request.Headers.Add("Origin", Attacker);

        using var response = await client.SendAsync(request);

        // The request itself still runs — CORS is not authorization, and the server has no
        // obligation to refuse it. What matters is that the *browser* is never told it may
        // hand the body to script.
        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task No_origin_is_allowed_when_none_is_configured()
    {
        using var client = CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousPath);
        request.Headers.Add("Origin", Allowed);

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_preflight_from_an_allowed_origin_permits_the_headers_the_web_app_sends()
    {
        using var client = CreateClient(Allowed);

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/sales");
        request.Headers.Add("Origin", Allowed);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add(
            "Access-Control-Request-Headers",
            $"authorization,content-type,{IdempotencyFilter.HeaderName},{OverrideGrantService.HeaderName}");

        using var response = await client.SendAsync(request);

        var allowed = string.Join(
            ',',
            response.Headers.GetValues("Access-Control-Allow-Headers"));

        // A completed sale is the one request that carries all of them at once: the
        // cashier's bearer token, the idempotency key that makes a retry safe, and a
        // manager's override grant when a price was changed.
        Assert.Contains(IdempotencyFilter.HeaderName, allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(OverrideGrantService.HeaderName, allowed, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_preflight_from_an_unlisted_origin_is_not_approved()
    {
        using var client = CreateClient(Allowed);

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/sales");
        request.Headers.Add("Origin", Attacker);
        request.Headers.Add("Access-Control-Request-Method", "POST");

        using var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task The_replay_header_is_readable_across_origins()
    {
        using var client = CreateClient(Allowed);

        using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousPath);
        request.Headers.Add("Origin", Allowed);

        using var response = await client.SendAsync(request);

        var exposed = string.Join(',', response.Headers.GetValues("Access-Control-Expose-Headers"));

        // Without this the till cannot tell "that sale was already recorded" from "sale
        // recorded" after a timeout and a retry — which is the exact moment the
        // distinction decides whether somebody gets charged twice.
        Assert.Contains(
            IdempotencyFilter.ReplayHeaderName,
            exposed,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Credentials_are_never_allowed()
    {
        // Asserted against the policy the application actually built, not against the
        // source that built it.
        var policy = await ResolvePolicyAsync(Allowed);

        // Tokens travel in the Authorization header from sessionStorage, never in a
        // cookie (DECISIONS.md, Phase 4.2, confirmed in 8.2). Turning this on is what
        // would make a future cookie ambient on cross-site requests and demand a CSRF
        // story that nothing here has.
        Assert.False(policy.SupportsCredentials);
    }

    [Fact]
    public async Task The_policy_allows_no_origin_it_was_not_given()
    {
        var policy = await ResolvePolicyAsync(Allowed);

        Assert.False(policy.AllowAnyOrigin);
        Assert.Equal([Allowed], policy.Origins);
    }

    /// <summary>
    /// Every header constant declared in the API is either allow-listed for CORS or named
    /// here as deliberately excluded.
    /// </summary>
    /// <remarks>
    /// The drift guard. A new header added to an endpoint works perfectly in development,
    /// where Vite's proxy makes everything same-origin and CORS never runs — and then
    /// fails preflight in production on whichever single flow uses it. Nobody remembers to
    /// update a CORS list; a failing build does.
    /// </remarks>
    [Fact]
    public void Every_request_header_the_api_declares_is_allow_listed()
    {
        var declared = typeof(PosCors).Assembly
            .GetTypes()
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy))
            .Where(field => field is { IsLiteral: true, IsInitOnly: false }
                            && field.FieldType == typeof(string)
                            && field.Name.EndsWith("HeaderName", StringComparison.Ordinal))
            .Select(field => (string?)field.GetRawConstantValue())
            .Where(value => value is not null)
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Response headers, which belong on the exposed list rather than the allowed one.
        string[] responseOnly = [IdempotencyFilter.ReplayHeaderName];

        var missing = declared
            .Except(responseOnly, StringComparer.OrdinalIgnoreCase)
            .Except(PosCors.AllowedHeaders, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.True(
            missing.Length == 0,
            $"These request headers are declared by the API but are not in PosCors.AllowedHeaders, " +
            $"so a cross-origin browser will fail preflight on whichever endpoint reads them: " +
            $"{string.Join(", ", missing)}");

        foreach (var header in responseOnly)
        {
            Assert.Contains(header, PosCors.ExposedHeaders, StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>The policy as the running application built it, resolved out of its own DI.</summary>
    private async Task<CorsPolicy> ResolvePolicyAsync(params string[] origins)
    {
        var configured = factory.WithWebHostBuilder(builder =>
        {
            for (var i = 0; i < origins.Length; i++)
            {
                builder.UseSetting($"{CorsOptions.Keys.AllowedOrigins}:{i}", origins[i]);
            }
        });

        // The host is built lazily; nothing exists to resolve from until a client forces it.
        using var _ = configured.CreateClient();

        var provider = configured.Services.GetRequiredService<ICorsPolicyProvider>();

        var policy = await provider.GetPolicyAsync(new DefaultHttpContext(), PosCors.PolicyName);

        Assert.NotNull(policy);
        return policy;
    }
}
