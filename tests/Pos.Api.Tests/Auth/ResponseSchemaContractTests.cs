using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// Every endpoint that returns a body declares what that body is.
/// </summary>
/// <remarks>
/// OpenAPI infers a response schema from the handler's declared return type. A handler typed
/// <c>Task&lt;IResult&gt;</c> tells it nothing, so the generated document carries an operation
/// with <b>no response content at all</b> — and <c>openapi-typescript</c> then types that
/// body as <c>never</c> in <c>src/Pos.Web/src/api/schema.d.ts</c>.
/// <para>
/// This is worse than it sounds. It does not fail the backend build, it does not fail any
/// functional test, and the endpoint keeps returning exactly the right JSON. It surfaces only
/// as a frontend that cannot name the shape it is receiving — at which point the pressure is
/// to hand-write the DTO, which CLAUDE.md forbids precisely because hand-written DTOs drift
/// and a renamed field becomes <c>undefined</c> at runtime.
/// </para>
/// <para>
/// Found by Phase 4.1: all five <c>/auth</c> endpoints returned <c>Task&lt;IResult&gt;</c>, so
/// the login and <c>/me</c> bodies — the first two calls any client makes — were untyped.
/// Fixed by giving them <c>Results&lt;…&gt;</c> unions.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ResponseSchemaContractTests(PosApiFactory factory)
{
    /// <summary>
    /// Routes that legitimately return a body no schema describes.
    /// </summary>
    /// <remarks>
    /// The health checks write a plain-text status through the health-check middleware rather
    /// than returning a result, so there is no return type to infer from and nothing for a
    /// client to deserialise. They are unversioned and outside <c>/api/v1</c>.
    /// <para>
    /// <c>/openapi/{documentName}.json</c> is the document itself. It is not part of the API
    /// contract, is not routed in Production, and describing itself would be circular.
    /// </para>
    /// </remarks>
    private static readonly string[] ExemptRoutes =
    [
        "/health/live",
        "/health/ready",
        "/openapi/{documentName}.json",
    ];

    [Fact]
    public void Every_endpoint_that_returns_a_body_declares_its_schema()
    {
        var endpoints = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.NotEmpty(endpoints);

        var offenders = new List<string>();

        foreach (var endpoint in endpoints)
        {
            var route = "/" + endpoint.RoutePattern.RawText?.TrimStart('/');

            if (ExemptRoutes.Contains(route, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            // 3xx is not a body, and 4xx is problem+json — one shape for the whole API,
            // documented once, and not per-endpoint.
            var successes = endpoint.Metadata
                .GetOrderedMetadata<IProducesResponseTypeMetadata>()
                .Where(metadata => metadata.StatusCode is >= 200 and < 300)
                .ToArray();

            if (successes.Length == 0)
            {
                // Nothing declared at all, which is what a bare `IResult` produces.
                offenders.Add($"{route} (no 2xx response declared)");
                continue;
            }

            foreach (var metadata in successes)
            {
                // 204 having no type is the correct and only possible answer for it.
                if (metadata.StatusCode == StatusCodes.Status204NoContent)
                {
                    continue;
                }

                if (metadata.Type is null || metadata.Type == typeof(void))
                {
                    offenders.Add($"{route} → {metadata.StatusCode} (declared with no type)");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "These endpoints return a body the OpenAPI document cannot describe, so the "
            + "generated TypeScript client types it as `never`. Give the handler a "
            + "Results<...> return type instead of IResult: "
            + string.Join("; ", offenders.Order(StringComparer.Ordinal)));
    }
}
