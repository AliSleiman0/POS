using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// Where the OpenAPI document is served, and where it is not.
/// </summary>
/// <remarks>
/// It is routed in Development and Testing — Testing so contract tests can read the real
/// document — and in neither case does it expose tenant data. It must not be routed in
/// Production: it is anonymous by necessity (a generator fetches it before it has a token) and
/// it enumerates every route, parameter and schema in the application. Handing an
/// unauthenticated caller a complete map is not a breach on its own, but it is free
/// reconnaissance, and there is no reason for a shop's till server to publish one.
/// <para>
/// The environment check is one line in <c>Program.cs</c> and would be trivial to widen by
/// accident while adding a third environment. This is the test that notices.
/// </para>
/// </remarks>
public sealed class OpenApiRoutingTests
{
    [Theory]
    [InlineData("Development", true)]
    [InlineData("Testing", true)]
    [InlineData("Production", false)]
    [InlineData("Staging", false)]
    public void The_document_is_not_served_outside_development_and_testing(
        string environment,
        bool expected)
    {
        using var factory = new EnvironmentFactory(environment);

        // Force the host to build. The endpoint table is what is being asserted, not a
        // response — a request would also need a database, which Production would not have.
        var routes = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        var routed = routes.Any(route =>
            route.Contains("/openapi/", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(expected, routed);

        // Scalar renders the document for a human and is Development-only regardless: it is a
        // UI, and nothing automated needs it.
        var scalar = routes.Any(route => route.Contains("scalar", StringComparison.OrdinalIgnoreCase));

        Assert.Equal(environment == "Development", scalar);
    }

    /// <summary>
    /// A host in a named environment, with the test database wired up so the composition root
    /// can be built at all.
    /// </summary>
    private sealed class EnvironmentFactory(string environment) : WebApplicationFactory<Program>
    {
        protected override IWebHostBuilder? CreateWebHostBuilder()
        {
            var builder = base.CreateWebHostBuilder();
            builder?.UseEnvironment(environment);
            return builder;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);

            builder.UseEnvironment(environment);

            // Program.cs throws at startup on a missing connection string, by design. The
            // value is never connected to — only the endpoint table is read.
            builder.UseSetting(
                "ConnectionStrings:Postgres",
                "Host=localhost;Database=unused;Username=unused;Password=unused");

            // Same for the signing key: ValidateOnStart refuses to build a host without a
            // usable one, which is the behaviour a deploy relies on.
            builder.UseSetting("Jwt:SigningKey", new string('k', 64));
            builder.UseSetting("Jwt:Issuer", "pos-tests");
            builder.UseSetting("Jwt:Audience", "pos-tests");
        }
    }
}
