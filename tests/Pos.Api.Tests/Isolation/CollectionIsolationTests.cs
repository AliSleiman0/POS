using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

/// <summary>
/// Checklist item 1: every collection endpoint, as tenant B, returns none of tenant A's rows.
/// </summary>
/// <remarks>
/// The two tenants hold identical data, so the assertion can be exact set equality rather
/// than a search for a leaked name: if isolation broke, every list here would come back
/// with twice as many rows. Asserting both what must be present and what must be absent is
/// deliberate — a list assertion that only checks for absence passes when the endpoint is
/// broken and returns nothing at all.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CollectionIsolationTests(PosApiFactory factory)
{
    public static TheoryData<string> Collections => IsolationManifest.KeysOf(IsolationKind.Collection);

    [Theory]
    [MemberData(nameof(Collections))]
    public async Task A_collection_returns_this_tenants_rows_and_only_this_tenants_rows(string key)
    {
        var testCase = IsolationManifest.ByKey[key];
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(testCase.Caller, world);

        var response = await client.SendAsync(testCase, world, testCase.UrlFor(Guid.Empty));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        var items = testCase.Paginated ? body.GetProperty("items") : body;

        Assert.Equal(JsonValueKind.Array, items.ValueKind);

        if (testCase.Paginated)
        {
            // The set equality below compares against the whole tenant's rows, so it is only
            // a real assertion while the fixtures fit inside one page. If they ever grow past
            // the default limit, this fails loudly here instead of the equality quietly
            // starting to compare page one against everything.
            Assert.False(
                body.GetProperty("hasMore").GetBoolean(),
                $"{key} returned more than one page, so the assertion below no longer covers the tenant.");

            Assert.Equal(JsonValueKind.Null, body.GetProperty("nextCursor").ValueKind);
        }

        var returned = items.EnumerateArray()
            .Select(element => element.GetProperty("id").GetGuid())
            .Order()
            .ToArray();

        Assert.Equal(testCase.Expected!(world).Order(), returned);

        // Redundant given the equality above, and kept because it names the actual fear. If
        // the ids ever stop being the thing compared, this is the line that still fails.
        foreach (var id in testCase.Forbidden!(world))
        {
            Assert.DoesNotContain(id, returned);
        }
    }
}
