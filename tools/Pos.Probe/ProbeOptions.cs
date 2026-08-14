namespace Pos.Probe;

/// <summary>An expected, actionable failure. Anything else is a bug and keeps its stack.</summary>
public sealed class ProbeException(string message) : Exception(message);

/// <summary>Where to probe, as whom, and against which manifest.</summary>
public sealed record ProbeOptions
{
    public required Uri Api { get; init; }

    /// <summary>The manifest exported by <c>IsolationManifestExportTests</c>.</summary>
    public required string ManifestPath { get; init; }

    /// <summary>
    /// The tenant whose rows must never be visible. Seeded with data worth stealing.
    /// </summary>
    public required string VictimSlug { get; init; }

    /// <summary>The tenant the probe authenticates into. Every request is made as this one.</summary>
    public required string AttackerSlug { get; init; }

    public required string OwnerEmailPattern { get; init; }

    public required string Password { get; init; }

    public static string Usage => """
        Re-runs the isolation manifest against a deployed instance. Read-only.

        Usage:
          dotnet run --project tools/Pos.Probe -- --api <url> [options]

        Required:
          --api <url>            Base URL of the deployed API, e.g. https://pos-api.example.com

        Options:
          --manifest <path>      isolation-manifest.json, exported by the test suite.
                                 Default: tests/Pos.Api.Tests/bin/Release/net10.0/isolation-manifest.json
          --victim <slug>        Tenant whose rows must stay invisible. Default: probe-a
          --attacker <slug>      Tenant the probe logs in as.          Default: probe-b
          --owner-email <fmt>    Owner address, {slug} substituted.
                                 Default: owner@{slug}.example
          --password <string>    Shared by both owners. Or set $POS_PROBE_PASSWORD.

        Exits 0 when nothing leaked, 1 on any violation, 2 on a usage or setup error.
        """;

    public const string PasswordEnvironmentVariable = "POS_PROBE_PASSWORD";

    public static bool WantsHelp(string[] args) =>
        args.Length == 0 || args.Any(a =>
            string.Equals(a, "--help", StringComparison.Ordinal) ||
            string.Equals(a, "-h", StringComparison.Ordinal));

    public static ProbeOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (WantsHelp(args))
        {
            throw new ProbeException(Usage);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ProbeException($"Unexpected argument '{args[i]}'.");
            }

            if (i + 1 >= args.Length)
            {
                throw new ProbeException($"Option '{args[i]}' needs a value.");
            }

            values[args[i][2..]] = args[++i];
        }

        var api = Value(values, "api")
            ?? throw new ProbeException("--api is required. Try --help.");

        if (!Uri.TryCreate(api, UriKind.Absolute, out var parsed))
        {
            throw new ProbeException($"--api '{api}' is not an absolute URL.");
        }

        var password = Value(values, "password")
            ?? Environment.GetEnvironmentVariable(PasswordEnvironmentVariable)
            ?? throw new ProbeException(
                $"--password is required (or set ${PasswordEnvironmentVariable}). Prefer the " +
                "environment: an argument is visible in the shell history and to `ps`.");

        return new ProbeOptions
        {
            Api = parsed,
            ManifestPath = Value(values, "manifest")
                ?? Path.Combine("tests", "Pos.Api.Tests", "bin", "Release", "net10.0", "isolation-manifest.json"),
            VictimSlug = Value(values, "victim") ?? "probe-a",
            AttackerSlug = Value(values, "attacker") ?? "probe-b",
            OwnerEmailPattern = Value(values, "owner-email") ?? "owner@{slug}.example",
            Password = password,
        };
    }

    public string OwnerEmailFor(string slug) =>
        OwnerEmailPattern.Replace("{slug}", slug, StringComparison.Ordinal);

    private static string? Value(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && value.Length > 0 ? value : null;
}
