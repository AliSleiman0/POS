using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Common;

/// <summary>
/// What the API does about HTTPS when something else is terminating TLS in front of it.
/// </summary>
/// <remarks>
/// The failure this exists to prevent is total and silent. In a container there is no
/// HTTPS port; the edge proxy terminates TLS and forwards plain HTTP with
/// <c>X-Forwarded-Proto: https</c>. An app that still runs <c>UseHttpsRedirection</c>
/// there answers every single request with a 307 to the URL it was already on — the API
/// serves nothing at all, while the process stays up and every health check keeps passing.
/// <para>
/// <c>Hosting:BehindTlsTerminatingProxy</c> is the switch. It is a flag rather than an
/// environment check because "is something in front of me" is a deployment fact, not a
/// property of Production: the same image runs behind a proxy on the platform and directly
/// under <c>docker run</c> on a laptop.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class TlsTerminationTests(PosApiFactory factory)
{
    /// <summary>
    /// Anonymous, cheap, and mapped in every environment — so the assertion is about the
    /// pipeline rather than about anything an endpoint decided.
    /// </summary>
    private const string AnonymousPath = "/health/live";

    /// <summary>
    /// <c>HttpsRedirectionMiddleware</c> reads this configuration key to find the port to
    /// redirect to. Without it the middleware logs "Failed to determine the https port for
    /// redirect" and passes the request through, so a test that did not set it would see
    /// no redirect in either case and pass while asserting nothing.
    /// </summary>
    private const string HttpsPortKey = "HTTPS_PORT";

    private HttpClient CreateClient(bool behindProxy)
    {
        var configured = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting(
                "Hosting:BehindTlsTerminatingProxy",
                behindProxy ? "true" : "false");

            builder.UseSetting(HttpsPortKey, "443");
        });

        // Redirects must not be followed: the 307 *is* the observation. Following it would
        // turn the loop this guards against into a hang rather than a failed assertion.
        return configured.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
    }

    [Fact]
    public async Task Behind_a_terminating_proxy_a_forwarded_request_is_not_redirected()
    {
        using var client = CreateClient(behindProxy: true);

        using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousPath);
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        // The whole point. A redirect here is the outage described above.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Behind_a_terminating_proxy_a_request_without_the_header_is_still_not_redirected()
    {
        using var client = CreateClient(behindProxy: true);

        // No X-Forwarded-Proto at all — a platform health probe hitting the container
        // directly, for instance. It must be answered, not bounced: a probe that follows a
        // redirect to a port nothing is listening on marks the machine unhealthy and the
        // deploy rolls back for a reason that has nothing to do with the deploy.
        using var response = await client.GetAsync(AnonymousPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Without_the_flag_https_redirection_is_still_active()
    {
        using var client = CreateClient(behindProxy: false);

        using var response = await client.GetAsync(AnonymousPath);

        // The default is unchanged, which is what keeps `dotnet run` and every existing
        // test honest: turning the flag on is a deliberate statement about the topology,
        // not something that silently became the default for everyone.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(Uri.UriSchemeHttps, response.Headers.Location?.Scheme);
    }

    [Fact]
    public async Task Without_the_flag_a_forwarded_proto_header_is_not_trusted()
    {
        using var client = CreateClient(behindProxy: false);

        using var request = new HttpRequestMessage(HttpMethod.Get, AnonymousPath);
        request.Headers.Add("X-Forwarded-Proto", "https");

        using var response = await client.SendAsync(request);

        // Deliberate: the header is just a string a caller typed unless a proxy we trust
        // put it there. Honouring it by default would also make the remote address
        // spoofable, and RateLimitPolicies partitions unauthenticated PIN attempts on
        // exactly that — one attacker would get a fresh guessing budget per forged header.
        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
    }
}
