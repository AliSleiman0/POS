using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// The mechanism that makes this suite outlive Phase 1: every endpoint the application
/// maps must appear in <see cref="IsolationManifest"/>, or the build fails.
/// </summary>
/// <remarks>
/// Without this test the manifest is documentation, and documentation of which endpoints
/// are isolation-tested goes stale on the first Phase 2 pull request. With it, mapping an
/// endpoint and not deciding how its tenancy is proven is a red test with the route name in
/// the message. It is the sibling of
/// <c>AuthorizationContractTests.Every_endpoint_states_its_own_authorization</c>, and of
/// <c>RowLevelSecurityTests.Every_tenant_owned_table_is_covered</c> a layer below.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class EndpointCoverageTests(PosApiFactory factory)
{
    [Fact]
    public void Every_endpoint_has_a_row_in_the_isolation_manifest()
    {
        var routed = RoutedKeys();
        var declared = IsolationManifest.ByKey.Keys.ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(routed);

        var unaccounted = routed.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var stale = declared.Except(routed, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

        // Both directions. Missing rows are the leak this exists to prevent; stale rows mean
        // a theory is asserting against a route that no longer exists, which passes happily
        // and proves nothing.
        Assert.True(
            unaccounted.Length == 0 && stale.Length == 0,
            $"""
             The isolation manifest and the routing table disagree.

             Endpoints with no manifest row ({unaccounted.Length}):
               {string.Join("\n  ", unaccounted)}

             Manifest rows matching no endpoint ({stale.Length}):
               {string.Join("\n  ", stale)}

             Add a row in IsolationManifest.Cases for each endpoint above. If tenant
             isolation genuinely does not apply to it, say so with Kind = Exempt and an
             Exemption naming the test where the coverage does live.
             """);
    }

    [Fact]
    public void An_exempt_endpoint_says_where_its_coverage_lives()
    {
        var silent = IsolationManifest.Cases
            .Where(c => c.Kind == IsolationKind.Exempt && string.IsNullOrWhiteSpace(c.Exemption))
            .Select(c => c.Key)
            .ToArray();

        // "Exempt" with no reason is indistinguishable from "nobody got round to it", and
        // the difference is the entire value of the manifest as an audit record.
        Assert.True(
            silent.Length == 0,
            "Exempt without a stated reason: " + string.Join(", ", silent));
    }

    [Fact]
    public void A_tested_endpoint_is_fully_specified()
    {
        var incomplete = new List<string>();

        foreach (var c in IsolationManifest.Cases.Where(c => c.Kind != IsolationKind.Exempt))
        {
            if (c.Exemption is not null)
            {
                incomplete.Add($"{c.Key}: states an exemption but is not exempt");
            }

            if (c.Kind == IsolationKind.Collection && (c.Expected is null || c.Forbidden is null))
            {
                incomplete.Add($"{c.Key}: a collection needs both the ids it must return and the ids it must not");
            }

            if (c.Kind == IsolationKind.ById && c.VictimId is null)
            {
                incomplete.Add($"{c.Key}: a by-id route needs the tenant A row it reaches for");
            }

            if (c.Kind == IsolationKind.ById && !c.Template.Contains('{', StringComparison.Ordinal))
            {
                incomplete.Add($"{c.Key}: a by-id route needs a route parameter to substitute into");
            }

            if (c.Refused.Length == 0)
            {
                incomplete.Add($"{c.Key}: no callers listed as refused, so nothing is negatively tested");
            }
        }

        // Paginated describes how a collection's body is shaped, so it means nothing
        // anywhere else. Set on a by-id row it would read as a claim the theories silently
        // ignore, which is how a manifest stops being trustworthy as a record.
        foreach (var c in IsolationManifest.Cases.Where(c => c.Paginated && c.Kind != IsolationKind.Collection))
        {
            incomplete.Add($"{c.Key}: Paginated only means something for a collection");
        }

        Assert.True(incomplete.Count == 0, string.Join("\n", incomplete));
    }

    /// <summary>Every routable (method, pattern) pair, keyed the way the manifest keys them.</summary>
    private HashSet<string> RoutedKeys()
    {
        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        return source.Endpoints
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint =>
            {
                var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
                var path = endpoint.RoutePattern.RawText?.Trim('/') ?? string.Empty;

                // An endpoint mapped for every verb still needs one decision, not none.
                return (methods is { Count: > 0 } ? methods : ["ANY"])
                    .Select(method => $"{method} {path}");
            })
            .ToHashSet(StringComparer.Ordinal);
    }
}
