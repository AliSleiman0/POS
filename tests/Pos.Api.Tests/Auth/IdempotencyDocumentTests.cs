using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Idempotency;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// The generated OpenAPI document declares <c>Idempotency-Key</c> on exactly the endpoints
/// that require it.
/// </summary>
/// <remarks>
/// Two lists that must agree: the routing table's <see cref="IdempotentEndpointMetadata"/>
/// markers, and the header parameters in the document. They come from the same call —
/// <c>RequireIdempotency</c> attaches the marker, and <c>IdempotencyOperationTransformer</c>
/// reads it — so this asserts the wiring holds end to end rather than that two hand-written
/// lists match.
/// <para>
/// Found by Phase 4.1: the header was enforced by an endpoint filter and documented in
/// docs/API.md, but appeared nowhere in the OpenAPI document. The generated TypeScript client
/// therefore typed <c>params.header</c> as <c>undefined</c> on every money- and stock-moving
/// call, leaving no typed way to send the one header that makes a retry safe. Nothing on the
/// backend noticed, because the filter worked perfectly for anyone who sent it by hand.
/// </para>
/// <para>
/// Read over HTTP from the document the app actually serves, not from a rebuilt
/// approximation. An approximation would have agreed with itself and missed exactly this.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class IdempotencyDocumentTests(PosApiFactory factory)
{
    [Fact]
    public async Task Every_idempotent_endpoint_declares_the_header_in_the_document()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        var declaringTheHeader = new HashSet<string>(StringComparer.Ordinal);

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var operation in path.Value.EnumerateObject())
            {
                if (!operation.Value.TryGetProperty("parameters", out var parameters))
                {
                    continue;
                }

                foreach (var parameter in parameters.EnumerateArray())
                {
                    var name = parameter.GetProperty("name").GetString();
                    var location = parameter.GetProperty("in").GetString();

                    if (!string.Equals(name, IdempotencyFilter.HeaderName, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(location, "header", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    // Optional would be worse than absent: a generator would make it
                    // omittable, and omitting it is a 400 rather than a non-idempotent
                    // success — so the type would say "safe to leave out" about the one
                    // header that must not be left out.
                    Assert.True(
                        parameter.TryGetProperty("required", out var required) && required.GetBoolean(),
                        $"{operation.Name} {path.Name} declares Idempotency-Key but not as required.");

                    declaringTheHeader.Add($"{operation.Name.ToUpperInvariant()} {path.Name.Trim('/')}");
                }
            }
        }

        var enforcingTheHeader = IdempotentRoutes();

        Assert.NotEmpty(enforcingTheHeader);

        var undocumented = enforcingTheHeader
            .Except(declaringTheHeader, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var overdocumented = declaringTheHeader
            .Except(enforcingTheHeader, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undocumented.Length == 0,
            "These endpoints require Idempotency-Key but the OpenAPI document does not mention "
            + "it, so a generated client has no typed way to send it: "
            + string.Join(", ", undocumented));

        Assert.True(
            overdocumented.Length == 0,
            "The document declares Idempotency-Key on endpoints that do not enforce it, which "
            + "promises a replay guarantee that does not exist: "
            + string.Join(", ", overdocumented));
    }

    /// <summary>Routes carrying the marker, as "METHOD path".</summary>
    private HashSet<string> IdempotentRoutes()
    {
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        return source.Endpoints
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<IdempotentEndpointMetadata>() is not null)
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];

                // Route constraints are not in the document's path keys: the router says
                // "{id:guid}" and OpenAPI says "{id}".
                var path = StripConstraints(endpoint.RoutePattern.RawText?.Trim('/') ?? string.Empty);

                return methods.Select(method => $"{method} {path}");
            })
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string StripConstraints(string path) => string.Join(
        '/',
        path.Split('/').Select(segment => segment.StartsWith('{') && segment.Contains(':', StringComparison.Ordinal)
            ? string.Concat(segment.AsSpan(0, segment.IndexOf(':', StringComparison.Ordinal)), "}")
            : segment));
}
