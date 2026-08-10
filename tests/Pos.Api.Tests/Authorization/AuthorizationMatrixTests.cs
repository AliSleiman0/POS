using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Authorization;

/// <summary>
/// The phase's real deliverable: proof that every endpoint admits exactly the roles the policy
/// map says it should, and no others.
/// </summary>
/// <remarks>
/// <b>Derived, not listed.</b> The grid is computed from the routing table crossed with
/// <c>PolicyCatalog.RolesByPolicy</c>, so an endpoint mapped tomorrow is covered the day it
/// ships and there is no second list to keep in step. A hand-written table would verify
/// today's endpoints and quietly stop being exhaustive on the next pull request — which is the
/// failure mode that makes an authorization suite worth less than the confidence it creates.
/// <para>
/// <b>What this cannot catch, and what does.</b> Because the expectation is read off the
/// endpoint's own metadata, changing which policy an endpoint requires changes the expectation
/// with it — verified by moving <c>GET /audit</c> from <c>CanManageEmployees</c> to
/// <c>CanSell</c>, which left all 229 cases green. The wrong-policy question is answered by two
/// other layers, both of which did go red on that change: the <c>Refused</c> lists in
/// <c>IsolationManifest</c>, which name the callers an endpoint must turn away regardless of
/// what it claims to require, and hand-written tests such as
/// <c>AuditReadTests.A_manager_cannot_read_the_audit_log</c> for the choices that would be
/// expensive to get wrong. This test's job is exhaustiveness — that no (role × endpoint) pair
/// goes unprobed — not adjudicating the policy map.
/// </para>
/// <para>
/// The independent check on the policy map itself is elsewhere and stays there:
/// <c>AuthorizationContractTests.The_policy_map_matches_the_table_in_ARCHITECTURE_md</c> reads
/// the documented table. This test would happily agree with a catalog that had been changed by
/// mistake; that one would not.
/// </para>
/// <para>
/// <b>The negative direction is pinned exactly and the positive one is not.</b> A refused
/// caller must get 401 or 403 specifically, per the rules below. A permitted caller is asserted
/// only to get <i>neither</i> — 404 for a <c>Guid.Empty</c> id, 400 for an empty body, both of
/// which prove authorization let them through. That is what lets the whole matrix run without
/// writing a single row: nothing is created, so nothing accumulates and no fixture has to be
/// reset between runs.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class AuthorizationMatrixTests(PosApiFactory factory)
{
    /// <summary>
    /// The two routes the matrix does not probe.
    /// </summary>
    /// <remarks>
    /// Both spend something real per attempt: <c>/auth/pin</c> burns one of a user's five
    /// lockout attempts, and both are rate-limited on the device token, ten per minute. Probing
    /// them with four callers would lock the world's users out and make whichever unrelated
    /// test ran next fail depending on ordering. <c>IsolationManifest</c> excludes them for the
    /// same reason; their authorization is pinned by hand in <c>PinLoginTests</c> and
    /// <c>SaleOverrideTests</c>.
    /// </remarks>
    private static readonly string[] Unprobed =
    [
        "POST api/v1/auth/pin",
        "POST api/v1/auth/override",
    ];

    public static TheoryData<string, string> RoleAndEndpoint
    {
        get
        {
            var data = new TheoryData<string, string>();

            foreach (var key in IsolationManifest.ByKey.Keys.Where(k => !Unprobed.Contains(k, StringComparer.Ordinal)))
            {
                foreach (var role in RoleNames.All)
                {
                    data.Add(role, key);
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(RoleAndEndpoint))]
    public async Task An_endpoint_admits_exactly_the_roles_its_policy_names(string role, string key)
    {
        var world = await factory.MatrixWorldAsync();
        var expectation = await ExpectationForAsync(key);

        if (expectation is Expectation.Anonymous)
        {
            // Nothing to assert per role — the anonymous routes have their own test below.
            return;
        }

        using var client = factory.CreateClient();
        client.WithBearer((await client.LoginAsync(
            world.Slug, AuthorizationMatrixWorld.EmailFor(role), AuthorizationMatrixWorld.Password)).AccessToken);

        using var response = await SendAsync(client, key);

        switch (expectation)
        {
            case Expectation.DeviceOnly:
                // The wrong *scheme*, not the wrong role — no JWT identity satisfies a policy
                // that names the device handler, whatever role it carries. 401 rather than
                // 403, and the distinction matters: a 403 would tell a till operator to go and
                // find a manager for something no role can fix.
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
                break;

            case Expectation.Policy policy when policy.Roles.Contains(role, StringComparer.Ordinal):
                Assert.False(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{key} refused {role} with {(int)response.StatusCode}, but {policy.Name} admits it.");
                break;

            case Expectation.Policy policy:
                Assert.True(
                    response.StatusCode == HttpStatusCode.Forbidden,
                    $"{key} answered {role} with {(int)response.StatusCode}; {policy.Name} "
                    + "does not admit that role, so it should have been 403.");
                break;

            case Expectation.AuthenticatedOnly:
                Assert.False(
                    response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                    $"{key} refused an authenticated {role} with {(int)response.StatusCode}, "
                    + "but it requires only authentication.");
                break;

            default:
                throw new InvalidOperationException($"Unhandled expectation for {key}.");
        }
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task An_endpoint_that_needs_a_caller_refuses_an_anonymous_one(string key)
    {
        var expectation = await ExpectationForAsync(key);

        if (expectation is Expectation.Anonymous)
        {
            return;
        }

        using var client = factory.CreateClient();
        using var response = await SendAsync(client, key);

        // 401, never 403. "You are not signed in" and "you are signed in and may not do this"
        // are different instructions to whoever is reading, and a client that cannot tell them
        // apart offers a login form to somebody already logged in.
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public static TheoryData<string> Endpoints
    {
        get
        {
            var data = new TheoryData<string>();

            foreach (var key in IsolationManifest.ByKey.Keys.Where(k => !Unprobed.Contains(k, StringComparer.Ordinal)))
            {
                data.Add(key);
            }

            return data;
        }
    }

    [Fact]
    public async Task The_matrix_covers_every_role_and_is_not_vacuous()
    {
        // Guards against the whole suite quietly passing because the expectation resolver
        // classified everything as Anonymous and every case returned early.
        var expectations = new List<Expectation>();

        foreach (var key in IsolationManifest.ByKey.Keys)
        {
            expectations.Add(await ExpectationForAsync(key));
        }

        Assert.Contains(expectations, e => e is Expectation.Policy);
        Assert.Contains(expectations, e => e is Expectation.DeviceOnly);
        Assert.Contains(expectations, e => e is Expectation.Anonymous);

        // And the roles being probed are the roles that exist, so adding a fourth to
        // PolicyCatalog cannot leave it silently untested.
        var roles = PolicyCatalog.RolesByPolicy.Values.SelectMany(r => r).Distinct().Order().ToArray();

        Assert.Equal(RoleNames.All.Order(), roles);
    }

    /// <summary>What the routing table itself says about who may call an endpoint.</summary>
    private abstract record Expectation
    {
        /// <summary>Explicitly open — no assertion to make per role.</summary>
        public sealed record Anonymous : Expectation;

        /// <summary>Gated on a named policy, whose roles come from the catalog.</summary>
        public sealed record Policy(string Name, IReadOnlyList<string> Roles) : Expectation;

        /// <summary>Gated on the device-token scheme, which no JWT satisfies.</summary>
        public sealed record DeviceOnly : Expectation;

        /// <summary>Requires a caller but names no policy.</summary>
        public sealed record AuthenticatedOnly : Expectation;
    }

    private async Task<Expectation> ExpectationForAsync(string key)
    {
        var endpoint = await FindAsync(key);

        if (endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null)
        {
            return new Expectation.Anonymous();
        }

        // Plural: RegisterEndpoints applies its policy at the *group* level, and a route may
        // carry more than one piece of authorization metadata.
        var policies = endpoint.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Select(data => data.Policy)
            .Where(name => !string.IsNullOrEmpty(name))
            .ToArray();

        if (policies.Length == 0)
        {
            return new Expectation.AuthenticatedOnly();
        }

        if (policies.Contains(DeviceTokenAuthenticationHandler.PolicyName, StringComparer.Ordinal))
        {
            return new Expectation.DeviceOnly();
        }

        var name = policies[0]!;

        Assert.True(
            PolicyCatalog.RolesByPolicy.TryGetValue(name, out var roles),
            $"{key} requires policy '{name}', which is not in PolicyCatalog. Either add it "
            + "there (and to the table in docs/ARCHITECTURE.md), or use one that exists.");

        return new Expectation.Policy(name, roles!);
    }

    private async Task<RouteEndpoint> FindAsync(string key)
    {
        // Resolved through the factory so the host is built, which is also what the isolation
        // suite does. The endpoints are static once the app is up.
        _ = await factory.MatrixWorldAsync();

        var source = factory.Services.GetRequiredService<EndpointDataSource>();

        var endpoint = source.Endpoints
            .OfType<RouteEndpoint>()
            .FirstOrDefault(candidate =>
            {
                var methods = candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"];
                var path = candidate.RoutePattern.RawText?.Trim('/') ?? string.Empty;

                return methods.Any(method => string.Equals($"{method} {path}", key, StringComparison.Ordinal));
            });

        Assert.NotNull(endpoint);

        return endpoint;
    }

    /// <summary>
    /// Sends the probe, with <see cref="Guid.Empty"/> in every route parameter.
    /// </summary>
    /// <remarks>
    /// An id that exists in no tenant, so a permitted caller reaches the handler and is
    /// answered 404 — proving authorization passed without anything being written. Bodies are
    /// deliberately empty: a permitted collection POST then fails validation with a 400, which
    /// is equally good evidence and equally harmless.
    /// </remarks>
    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string key)
    {
        var testCase = IsolationManifest.ByKey[key];
        var url = testCase.UrlFor(Guid.Empty);

        return testCase.Method switch
        {
            "GET" => client.GetAsync(new Uri(url, UriKind.Relative)),

            // A fresh key on 🔒 routes, so the probe reaches the authorization check rather
            // than being turned away with a 400 on the missing header.
            "POST" when testCase.Idempotent => client.PostIdempotentAsync(url, new { }),
            "POST" => client.PostAsJsonAsync(url, new { }),
            "PUT" => client.PutAsJsonAsync(url, new { }),
            "DELETE" => client.DeleteAsync(new Uri(url, UriKind.Relative)),
            _ => throw new NotSupportedException($"The matrix cannot send '{testCase.Method}' yet."),
        };
    }
}
