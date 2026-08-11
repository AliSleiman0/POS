using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Pos.Probe;

/// <summary>One manifest row as the exporter wrote it.</summary>
public sealed record ProbeCase(string Key, string Kind, IReadOnlyList<string> Refused, bool Paginated, bool Idempotent);

/// <summary>What the probe found.</summary>
public sealed class ProbeReport
{
    public List<string> Violations { get; } = [];
    public List<string> Checked { get; } = [];
    public List<string> Skipped { get; } = [];

    public void Write(TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine();
        writer.WriteLine($"Checked   {Checked.Count} claims against the deployed instance");

        foreach (var line in Checked)
        {
            writer.WriteLine($"  ok    {line}");
        }

        if (Skipped.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine($"Skipped   {Skipped.Count} — no way to address them from outside");

            foreach (var line in Skipped)
            {
                writer.WriteLine($"  --    {line}");
            }
        }

        writer.WriteLine();

        if (Violations.Count == 0)
        {
            writer.WriteLine("NO CROSS-TENANT VISIBILITY. Every claim the manifest makes held in production.");
            return;
        }

        writer.WriteLine($"*** {Violations.Count} VIOLATION(S) ***");

        foreach (var line in Violations)
        {
            writer.WriteLine($"  FAIL  {line}");
        }
    }
}

/// <summary>
/// Attacks a deployed instance with one tenant's credentials, looking for another's rows.
/// </summary>
/// <remarks>
/// It knows only what a hostile client knows — a URL, a login, and the manifest's claims. It
/// shares no types with the application, so it cannot pass by agreeing with a bug.
/// <para>
/// <b>Read-only.</b> Every request is a GET. The manifest's write rows are reported as
/// skipped rather than attempted: a probe that POSTed to prove isolation would be writing
/// into a live shop's ledger to make a point, and the ledger is append-only.
/// </para>
/// </remarks>
public sealed class IsolationProbe(ProbeOptions options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Matches a route parameter such as <c>{id:guid}</c> or <c>{code}</c>.</summary>
    private static readonly Regex Parameter = new(@"\{[^}]+\}", RegexOptions.Compiled);

    public async Task<ProbeReport> RunAsync()
    {
        var report = new ProbeReport();

        if (!File.Exists(options.ManifestPath))
        {
            throw new ProbeException(
                $"No manifest at '{options.ManifestPath}'. Run the test suite first — " +
                "IsolationManifestExportTests writes it beside the test binary.");
        }

        var cases = JsonSerializer.Deserialize<ProbeCase[]>(
            await File.ReadAllTextAsync(options.ManifestPath), Json)
            ?? throw new ProbeException("The manifest could not be read.");

        // A probe that checked nothing and said "no violations" is the exact failure this
        // milestone exists to prevent, so an empty manifest is a hard error.
        if (cases.Length == 0)
        {
            throw new ProbeException("The manifest is empty. Refusing to report a pass on nothing.");
        }

        using var client = new HttpClient { BaseAddress = options.Api, Timeout = TimeSpan.FromSeconds(120) };

        var victim = await SignInAsync(client, options.VictimSlug);
        var attacker = await SignInAsync(client, options.AttackerSlug);

        // Everything the victim can see of itself. These are the ids that must never appear
        // in a response to the attacker, and the ids the attacker will ask for directly.
        var victimIds = await HarvestAsync(client, victim, cases);

        if (victimIds.Count == 0)
        {
            throw new ProbeException(
                $"Tenant '{options.VictimSlug}' has no rows to steal, so a pass would mean nothing. " +
                "Seed it with a product and a sale before probing.");
        }

        foreach (var probeCase in cases.Where(c => !c.Key.StartsWith("GET ", StringComparison.Ordinal)))
        {
            report.Skipped.Add($"{probeCase.Key} — a write; the probe is read-only");
        }

        foreach (var probeCase in cases.Where(c => c.Key.StartsWith("GET ", StringComparison.Ordinal)))
        {
            await CheckAsync(client, probeCase, attacker, victimIds, report);
        }

        await CheckAnonymousAsync(client, cases, report);

        return report;
    }

    private async Task CheckAsync(
        HttpClient client,
        ProbeCase probeCase,
        string attackerToken,
        IReadOnlyDictionary<string, IReadOnlyList<string>> victimIds,
        ProbeReport report)
    {
        var route = probeCase.Key["GET ".Length..];

        if (Parameter.IsMatch(route))
        {
            // A by-id route: ask for one of the victim's rows while holding the attacker's
            // token. The promise is 404 — not 403, which would confirm the row exists and
            // turn the endpoint into an existence oracle.
            var collection = CollectionOf(route);

            if (collection is null || !victimIds.TryGetValue(collection, out var ids) || ids.Count == 0)
            {
                report.Skipped.Add($"{probeCase.Key} — no victim id available to ask for");
                return;
            }

            var url = Parameter.Replace(route, ids[0], count: 1);

            if (Parameter.IsMatch(url))
            {
                report.Skipped.Add($"{probeCase.Key} — more parameters than the probe can address");
                return;
            }

            using var response = await SendAsync(client, url, attackerToken);

            if (response.StatusCode is HttpStatusCode.NotFound)
            {
                report.Checked.Add($"{probeCase.Key} — another tenant's row is 404");
            }
            else if (response.StatusCode is HttpStatusCode.OK)
            {
                report.Violations.Add(
                    $"{probeCase.Key} RETURNED 200 for tenant '{options.VictimSlug}'s row while " +
                    $"authenticated as '{options.AttackerSlug}'. Cross-tenant read.");
            }
            else
            {
                report.Checked.Add($"{probeCase.Key} — refused ({(int)response.StatusCode})");
            }

            return;
        }

        // A collection: fetch it as the attacker and look for any of the victim's ids.
        using var listed = await SendAsync(client, route, attackerToken);

        if (!listed.IsSuccessStatusCode)
        {
            report.Checked.Add($"{probeCase.Key} — refused ({(int)listed.StatusCode})");
            return;
        }

        var body = await listed.Content.ReadAsStringAsync();
        var leaked = victimIds.Values
            .SelectMany(ids => ids)
            .Where(id => body.Contains(id, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (leaked.Length > 0)
        {
            report.Violations.Add(
                $"{probeCase.Key} LEAKED {leaked.Length} of tenant '{options.VictimSlug}'s ids to " +
                $"'{options.AttackerSlug}': {string.Join(", ", leaked.Take(3))}");
        }
        else
        {
            report.Checked.Add($"{probeCase.Key} — no foreign ids in the response");
        }
    }

    /// <summary>
    /// Every GET route, unauthenticated. None may answer with data.
    /// </summary>
    /// <remarks>
    /// Cheap and worth doing separately: an endpoint that lost its authorization metadata
    /// would still pass every cross-tenant check above, because an anonymous caller has no
    /// tenant to cross out of.
    /// </remarks>
    private static async Task CheckAnonymousAsync(HttpClient client, IEnumerable<ProbeCase> cases, ProbeReport report)
    {
        foreach (var probeCase in cases.Where(c =>
                     c.Key.StartsWith("GET ", StringComparison.Ordinal) &&
                     !Parameter.IsMatch(c.Key)))
        {
            var route = probeCase.Key["GET ".Length..];

            using var response = await SendAsync(client, route, token: null);

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                report.Checked.Add($"anonymous {route} — refused ({(int)response.StatusCode})");
            }
            else
            {
                report.Violations.Add(
                    $"anonymous GET {route} answered {(int)response.StatusCode} rather than 401/403.");
            }
        }
    }

    /// <summary>Collects the victim's own ids, from its own responses.</summary>
    /// <remarks>
    /// Discovered rather than configured, which is what lets the probe inherit a new
    /// endpoint from the manifest without anybody hand-writing fixtures for it.
    /// </remarks>
    private static async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> HarvestAsync(
        HttpClient client,
        string victimToken,
        IEnumerable<ProbeCase> cases)
    {
        var harvested = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var probeCase in cases.Where(c =>
                     c.Key.StartsWith("GET ", StringComparison.Ordinal) &&
                     !Parameter.IsMatch(c.Key)))
        {
            var route = probeCase.Key["GET ".Length..];

            using var response = await SendAsync(client, route, victimToken);

            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var ids = IdsIn(await response.Content.ReadAsStringAsync());

            if (ids.Count > 0)
            {
                harvested[route] = ids;
            }
        }

        return harvested;
    }

    /// <summary>The collection route a by-id route belongs to: <c>a/b/{id}</c> → <c>a/b</c>.</summary>
    private static string? CollectionOf(string route)
    {
        var index = route.IndexOf('{', StringComparison.Ordinal);

        return index <= 0 ? null : route[..index].TrimEnd('/');
    }

    /// <summary>Every GUID in a response body, however deeply nested.</summary>
    private static IReadOnlyList<string> IdsIn(string body) =>
        [.. Regex.Matches(body, @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, string route, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/" + route.TrimStart('/'));

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(request);
    }

    private async Task<string> SignInAsync(HttpClient client, string slug)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            tenantSlug = slug,
            email = options.OwnerEmailFor(slug),
            password = options.Password,
        });

        if (!response.IsSuccessStatusCode)
        {
            throw new ProbeException(
                $"Could not sign in to '{slug}' as {options.OwnerEmailFor(slug)} " +
                $"({(int)response.StatusCode}). Both probe tenants must exist and share a password.");
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return body.GetProperty("accessToken").GetString()
            ?? throw new ProbeException($"Sign-in to '{slug}' returned no access token.");
    }
}
