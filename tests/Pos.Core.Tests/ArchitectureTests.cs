using System.Xml.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
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
    /// Members that read the machine's clock. Matched exactly, as
    /// <c>Namespace.Type::Member</c>.
    /// </summary>
    /// <remarks>
    /// <c>TimeProvider::get_System</c> is on the list and is the one worth explaining.
    /// Reaching the clock <i>through</i> <c>TimeProvider.System</c> is the same sin as
    /// reaching it directly — it just looks compliant, which makes it worse. Time enters
    /// Core as an injected <c>TimeProvider</c> or as an instant passed in as an argument,
    /// the way <c>RefreshToken.IsActive(DateTimeOffset now)</c> already does.
    /// </remarks>
    private static readonly string[] ForbiddenClockMembers =
    [
        "System.DateTime::get_Now",
        "System.DateTime::get_UtcNow",
        "System.DateTime::get_Today",
        "System.DateTimeOffset::get_Now",
        "System.DateTimeOffset::get_UtcNow",
        "System.TimeProvider::get_System",
    ];

    /// <summary>
    /// Core must not read the clock directly — time enters through TimeProvider so
    /// that business-day boundaries and shift arithmetic are testable at a fixed
    /// instant instead of depending on when the suite happens to run.
    /// </summary>
    /// <remarks>
    /// A real IL scan since Phase 3.1, which is the commit where Core first gained logic
    /// whose determinism is the product. Pricing, rounding and shift arithmetic all have to
    /// give the same answer on a Tuesday as on a Sunday, and a single <c>DateTime.UtcNow</c>
    /// buried in a helper would make one of them quietly untestable rather than failing.
    /// <para>
    /// Reflection cannot see this: a method body is not metadata, and a call to
    /// <c>DateTime.UtcNow</c> leaves no trace in <c>GetReferencedAssemblies</c> because
    /// <c>System.Runtime</c> is referenced regardless. The instruction stream is the only
    /// place the evidence exists.
    /// </para>
    /// </remarks>
    [Fact]
    public void Core_does_not_read_the_ambient_clock()
    {
        using var assembly = AssemblyDefinition.ReadAssembly(typeof(AssemblyMarker).Assembly.Location);

        var offenders = assembly.MainModule.Types
            .SelectMany(AllTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions
                .Where(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt
                    && instruction.Operand is MethodReference)
                .Select(instruction => (Method: method, Called: (MethodReference)instruction.Operand)))
            .Where(call => ForbiddenClockMembers.Contains(
                $"{call.Called.DeclaringType.FullName}::{call.Called.Name}",
                StringComparer.Ordinal))
            .Select(call =>
                $"{call.Method.DeclaringType.FullName}.{call.Method.Name} calls "
                + $"{call.Called.DeclaringType.FullName}::{call.Called.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Pos.Core read the ambient clock:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders)
            + Environment.NewLine
            + "Take the instant as a parameter, or inject a TimeProvider from the composition "
            + "root. The fix is to move the read outwards, not to relax this test.");
    }

    /// <summary>
    /// A type and everything nested inside it — compiler-generated closures, iterator state
    /// machines and async state machines are nested types, and a clock read inside a lambda
    /// lives in one of them rather than in the method that wrote it.
    /// </summary>
    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(AllTypes));

    /// <summary>
    /// Locates Pos.Core.csproj by walking up to the repo root, so the test does not
    /// depend on the working directory or on output-folder layout.
    /// </summary>
    /// <remarks>
    /// Anchors on the test assembly's output directory, NOT on <c>[CallerFilePath]</c>.
    /// <c>Directory.Build.props</c> sets <c>ContinuousIntegrationBuild</c> when <c>CI=true</c>,
    /// which turns on deterministic source paths — the compiler then bakes in
    /// <c>/_/tests/Pos.Core.Tests/ArchitectureTests.cs</c>, a path that exists nowhere on
    /// disk. A CallerFilePath-based walk therefore cannot find the root on any CI runner,
    /// which is the one environment where this guardrail has to work.
    /// </remarks>
    private static string CoreProjectPath()
    {
        var root = FindRepositoryRoot(AppContext.BaseDirectory)
            ?? throw new InvalidOperationException(
                $"Could not locate the repository root (Pos.slnx) walking up from '{AppContext.BaseDirectory}'.");

        var path = Path.Combine(root, "src", "Pos.Core", "Pos.Core.csproj");
        Assert.True(File.Exists(path), $"Expected Pos.Core.csproj at '{path}'.");
        return path;
    }

    /// <summary>Walks up from <paramref name="start"/> to the directory holding Pos.slnx.</summary>
    private static string? FindRepositoryRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pos.slnx")))
            {
                return dir.FullName;
            }
        }

        return null;
    }
}
