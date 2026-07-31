using System.Net;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// The other half of CLAUDE.md invariant 9: every endpoint gets a tenant-isolation test
/// <i>and</i> a negative authorization test.
/// </summary>
/// <remarks>
/// Driven off the same manifest as the isolation theories, so a Phase 2 endpoint gets both
/// the moment its row is added rather than only the one somebody remembered.
/// <para>
/// The assertion is 401 or 403 and deliberately not one specific code. Which of the two a
/// caller gets depends on whether they authenticated under the scheme the endpoint's policy
/// names — a bearer token at a <c>DeviceToken</c>-scheme endpoint is unauthenticated, so
/// 401; a cashier at an Owner-only endpoint is authenticated but unauthorized, so 403.
/// Deriving that here would add a second implementation of the authorization rules to keep
/// in step, without making the test catch anything more. The exact codes for today's
/// endpoints are pinned by hand in <c>RegisterEnrollmentTests</c> and <c>PinEligibleTests</c>.
/// </para>
/// <para>
/// By-id rows are probed with an id that exists in no tenant. If the authorization check
/// were ever removed, the response is a 404 — which fails this test — rather than a real
/// write against a real till.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class NegativeAuthorizationTests(PosApiFactory factory)
{
    public static TheoryData<string, Actor> RefusedCallers => IsolationManifest.RefusedCallers();

    [Theory]
    [MemberData(nameof(RefusedCallers))]
    public async Task An_endpoint_refuses_a_caller_that_does_not_hold_its_policy(string key, Actor actor)
    {
        var testCase = IsolationManifest.ByKey[key];
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(actor, world);

        // Guid.Empty: for a by-id route this addresses a row that exists in no tenant, so if
        // the authorization check were ever removed the answer is a 404 — which fails this
        // test — rather than a real write against a real till. For a collection the template
        // has nothing to substitute and the id is ignored.
        var response = await client.SendAsync(testCase, world, testCase.UrlFor(Guid.Empty));

        Assert.True(
            response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
            $"{key} answered {(int)response.StatusCode} {response.StatusCode} to {actor}, "
            + "which should have been refused with 401 or 403.");
    }
}
