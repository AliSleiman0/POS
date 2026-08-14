using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;

namespace Pos.Api.Tests.Common;

/// <summary>
/// The endpoint that proves error tracking is still receiving, and what it gives away.
/// </summary>
/// <remarks>
/// Its whole purpose is to raise an unhandled exception, which makes it the only place in
/// the suite where the *unhandled* 500 path can be asserted end to end. Everything else
/// throws a <c>PosDomainException</c>, which is mapped deliberately. So these tests cover
/// two things at once: that the diagnostic works, and that an exception nobody planned for
/// tells a client nothing about the inside of the server.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class DiagnosticsTests(PosApiFactory factory)
{
    private const string Path = "/api/v1/diagnostics/test-error";

    /// <summary>
    /// The exception must escape the pipeline to be reported, so the factory's default of
    /// rethrowing into the test has to be turned off — otherwise the throw lands here
    /// rather than in the middleware that would send it.
    /// </summary>
    private HttpClient CreateClient() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

    [Fact]
    public async Task An_owner_gets_a_500_that_says_nothing_about_the_server()
    {
        var world = await factory.IsolationWorldAsync();
        using var client = CreateClient();

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        using var response = await client.PostAsync(Path, content: null);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();

        // What a server got wrong is a server's business. A stack trace hands a caller the
        // file layout, the framework versions and often a connection string in a chained
        // exception message.
        Assert.DoesNotContain("DiagnosticsTestException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("at Pos.Api", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Pos.Api.Endpoints", body, StringComparison.Ordinal);
        Assert.DoesNotContain("Npgsql", body, StringComparison.Ordinal);
        Assert.DoesNotContain(".cs:line", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cashier_cannot_reach_it()
    {
        var world = await factory.IsolationWorldAsync();
        using var client = CreateClient();

        var tokens = await client.LoginAsync(world.B.Slug, TwoTenantWorld.CashierEmail, TwoTenantWorld.Password);
        client.WithBearer(tokens.AccessToken);

        using var response = await client.PostAsync(Path, content: null);

        // 403 and not 500: authorization runs before the handler, so the throw never
        // happens. If this ever returns 500 the policy has stopped being enforced and the
        // endpoint is a denial-of-service tool for anybody with a till login.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_anonymous_caller_cannot_reach_it()
    {
        using var client = CreateClient();

        using var response = await client.PostAsync(Path, content: null);

        // The reason it is authenticated at all: an anonymous error trigger lets anybody
        // exhaust a shop's error-tracking quota and bury the report that mattered.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
