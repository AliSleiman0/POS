using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pos.Api.Tests.Isolation;

/// <summary>One manifest row, reduced to what a probe outside this process can act on.</summary>
/// <remarks>
/// The manifest's real rows carry delegates — <c>Func&lt;TwoTenantWorld, Guid&gt;</c> and
/// friends — which only mean anything against a seeded in-process world. What survives the
/// journey is the route, the shape of the isolation claim, and who must be refused. That is
/// enough for a probe to attack a deployed instance, because it can discover the ids itself
/// from the victim tenant's own responses.
/// </remarks>
public sealed record ProbeCase(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("refused")] IReadOnlyList<string> Refused,
    [property: JsonPropertyName("paginated")] bool Paginated,
    [property: JsonPropertyName("idempotent")] bool Idempotent);

/// <summary>
/// Publishes the isolation manifest so something outside this assembly can replay it.
/// </summary>
/// <remarks>
/// Phase 8.6 requires the isolation suite to be re-run <b>against the deployed instance</b>,
/// because local row-level security passing proves nothing about production — a misconfigured
/// role is invisible until probed. The suite itself cannot travel: it is
/// <c>WebApplicationFactory</c> over a Testcontainer.
/// <para>
/// So the manifest travels instead, and <c>tools/Pos.Probe</c> replays it over HTTPS. The
/// point of exporting rather than hand-listing routes in the probe is that the manifest stays
/// the single source of truth: <c>EndpointCoverageTests</c> already fails the build when an
/// endpoint has no row, so a new endpoint is covered by the production probe for free, on the
/// day it is added, without anybody remembering.
/// </para>
/// </remarks>
public sealed class IsolationManifestExportTests
{
    /// <summary>Written beside the test binary; the probe is pointed at it.</summary>
    public const string FileName = "isolation-manifest.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    [Fact]
    public void The_manifest_is_published_for_the_production_probe()
    {
        var cases = IsolationManifest.Cases
            .Select(c => new ProbeCase(
                c.Key,
                c.Kind.ToString(),
                [.. c.Refused.Select(actor => actor.ToString())],
                c.Paginated,
                c.Idempotent))
            .OrderBy(c => c.Key, StringComparer.Ordinal)
            .ToArray();

        var path = Path.Combine(AppContext.BaseDirectory, FileName);

        File.WriteAllText(
            path,
            JsonSerializer.Serialize(cases, Indented));

        // The export is worthless if it silently empties. A probe handed an empty manifest
        // reports "no violations" and means "I checked nothing" — which is the failure mode
        // this whole exercise exists to avoid.
        Assert.True(
            cases.Length > 40,
            $"Only {cases.Length} cases were exported, which is far fewer than the manifest holds.");

        Assert.Contains(cases, c => string.Equals(c.Kind, "Collection", StringComparison.Ordinal));
        Assert.Contains(cases, c => string.Equals(c.Kind, "ById", StringComparison.Ordinal));
        Assert.True(File.Exists(path));
    }
}
