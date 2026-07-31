using System.Net;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// Checklist item 2: <c>/{resource}/{tenantA-id}</c> as tenant B answers 404, not 403.
/// </summary>
/// <remarks>
/// The distinction is the whole test. A 403 says "this id exists, and it is not yours",
/// which turns any by-id route into a way to ask whether a guessed id belongs to another
/// shop — and ids are not secrets. A 404 says only what a caller in tenant B is entitled to
/// know: there is no such thing here.
/// <para>
/// For a write, the status code is a promise and <c>AssertUntouched</c> is the evidence. A
/// handler that performed the write and then returned 404 would satisfy the first assertion
/// and fail the second.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ByIdIsolationTests(PosApiFactory factory)
{
    public static TheoryData<string> ByIdRoutes => IsolationManifest.KeysOf(IsolationKind.ById);

    [Theory]
    [MemberData(nameof(ByIdRoutes))]
    public async Task Another_tenants_resource_is_not_found_rather_than_forbidden(string key)
    {
        var testCase = IsolationManifest.ByKey[key];
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(testCase.Caller, world);

        var response = await client.SendAsync(testCase, world, testCase.UrlFor(testCase.VictimId!(world)));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        if (testCase.AssertUntouched is not null)
        {
            await testCase.AssertUntouched(factory, world);
        }
    }
}
