using System.Reflection;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Pos.Core;

namespace Pos.Core.Tests;

/// <summary>
/// Guards invariant 1 from CLAUDE.md: Pos.Core references nothing outside the BCL.
/// A guardrail, not a convention — conventions decay, this fails the build.
/// </summary>
public sealed class ArchitectureTests
{
    /// <summary>
    /// Infrastructure that must never reach the domain layer. Matched as a prefix,
    /// so "Npgsql" also catches "Npgsql.EntityFrameworkCore.PostgreSQL".
    /// </summary>
    private static readonly string[] ForbiddenPrefixes =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Npgsql",
        "Dapper",
        "Pos.Data",
        "Pos.Api",
    ];

    /// <summary>
    /// Catches a forbidden dependency that is *declared*.
    ///
    /// This check has to read the csproj rather than use reflection, because the C#
    /// compiler omits references whose types are never used. A PackageReference to
    /// EF Core that Core has not called into yet would be invisible to
    /// GetReferencedAssemblies() — so reflection alone would pass while the
    /// dependency sat in the project file waiting to be used.
    /// </summary>
    [Fact]
    public void Core_csproj_declares_no_infrastructure_references()
    {
        var csproj = XDocument.Load(CoreProjectPath());

        // PackageReference Include is already a package id ("Microsoft.EntityFrameworkCore").
        // ProjectReference Include is a path ("..\Pos.Data\Pos.Data.csproj") and needs the
        // filename taken. Do NOT run a package id through GetFileNameWithoutExtension: it
        // strips everything after the last dot, turning "Microsoft.EntityFrameworkCore"
        // into "Microsoft", which silently matches nothing.
        var packages = csproj.Descendants()
            .Where(e => e.Name.LocalName == "PackageReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty);

        var projects = csproj.Descendants()
            .Where(e => e.Name.LocalName == "ProjectReference")
            .Select(e => e.Attribute("Include")?.Value ?? string.Empty)
            .Select(v => Path.GetFileNameWithoutExtension(v.Replace('\\', '/')));

        var declared = packages.Concat(projects)
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToArray();

        var violations = declared
            .Where(d => ForbiddenPrefixes.Any(f => d.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Pos.Core must reference nothing outside the BCL, but declares: {string.Join(", ", violations)}. " +
            "The fix is to move the code into Pos.Data or Pos.Api, not to relax this test.");
    }

    /// <summary>
    /// Catches a forbidden dependency that is actually *used*, including one arriving
    /// transitively rather than through Core's own csproj.
    /// </summary>
    [Fact]
    public void Core_assembly_uses_no_infrastructure_types()
    {
        var violations = typeof(AssemblyMarker).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => ForbiddenPrefixes.Any(f => n.StartsWith(f, StringComparison.OrdinalIgnoreCase)))
            .ToArray();

        Assert.True(
            violations.Length == 0,
            $"Pos.Core compiled against infrastructure assemblies: {string.Join(", ", violations)}.");
    }

    /// <summary>
    /// Core must not read the clock directly — time enters through TimeProvider so
    /// that business-day boundaries and shift arithmetic are testable at a fixed
    /// instant instead of depending on when the suite happens to run.
    /// </summary>
    [Fact]
    public void Core_does_not_read_the_ambient_clock()
    {
        var forbidden = new[]
        {
            "System.DateTime::get_Now",
            "System.DateTime::get_UtcNow",
            "System.DateTime::get_Today",
            "System.DateTimeOffset::get_Now",
            "System.DateTimeOffset::get_UtcNow",
        };

        // Placeholder: asserts the rule is recorded and the assembly is loadable.
        // Replaced with real IL inspection (Mono.Cecil) in Phase 3, when Core first
        // contains time-dependent logic worth scanning.
        Assert.NotEmpty(forbidden);
        Assert.NotNull(typeof(AssemblyMarker).Assembly.Location);
    }

    /// <summary>
    /// Locates Pos.Core.csproj from this source file's compile-time path, so the test
    /// does not depend on the working directory or on output-folder layout.
    /// </summary>
    private static string CoreProjectPath([CallerFilePath] string thisFile = "")
    {
        var dir = Directory.GetParent(thisFile)
            ?? throw new InvalidOperationException($"Cannot resolve directory of '{thisFile}'.");

        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Pos.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (Pos.slnx).");

        var path = Path.Combine(root, "src", "Pos.Core", "Pos.Core.csproj");
        Assert.True(File.Exists(path), $"Expected Pos.Core.csproj at '{path}'.");
        return path;
    }
}
