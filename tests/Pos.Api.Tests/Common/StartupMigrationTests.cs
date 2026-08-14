using Mono.Cecil;
using Mono.Cecil.Cil;
using Pos.Data;

namespace Pos.Api.Tests.Common;

/// <summary>
/// Nothing that ships applies a migration. Migrations are a gated deploy step.
/// </summary>
/// <remarks>
/// With more than one API instance, concurrent startup migrations race: two processes
/// apply the same migration and the schema ends up in a state neither expected. It works
/// in development with a single instance, which is exactly why it survives to production
/// undetected — the first time it can fail is the first time it matters.
/// <para>
/// The rule holds today. This is what makes it keep holding: <c>Database.Migrate()</c> is
/// one line, it is the obvious fix for "the database is out of date" at the moment
/// somebody is annoyed about it, and nothing else in the build would notice.
/// </para>
/// <para>
/// An IL scan rather than a grep over source, because a grep matches the word in a
/// comment and in the exception message <c>DevSeeder</c> deliberately prints. It also
/// cannot see through a helper: a call routed via an extension method in another file is
/// still a call in the instruction stream.
/// </para>
/// <para>
/// Test assemblies are deliberately out of scope. <c>PosApiFactory</c> and
/// <c>PostgresFixture</c> both call <c>MigrateAsync</c> and both are correct to — a
/// throwaway container has to get a schema somehow, and the suite runs one process.
/// </para>
/// </remarks>
public sealed class StartupMigrationTests
{
    /// <summary>
    /// The members that apply schema changes.
    /// </summary>
    /// <remarks>
    /// <c>GetPendingMigrationsAsync</c> is deliberately absent: reading which migrations
    /// are outstanding is safe and useful, and <c>DevSeeder</c> uses it to refuse to seed
    /// a stale database rather than to fix one. Asking is allowed; applying is not.
    /// </remarks>
    private static readonly string[] ForbiddenMembers =
    [
        "Migrate",
        "MigrateAsync",
        "EnsureCreated",
        "EnsureCreatedAsync",
        "EnsureDeleted",
        "EnsureDeletedAsync",
    ];

    /// <summary>
    /// Types declaring them. Named so that an unrelated method called <c>Migrate</c> on
    /// somebody's own class does not fail the build.
    /// </summary>
    private static readonly string[] DatabaseFacadeTypes =
    [
        "Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions",
        "Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade",
        "Microsoft.EntityFrameworkCore.Migrations.IMigrator",
        "Microsoft.EntityFrameworkCore.Storage.IDatabaseCreator",
    ];

    /// <summary>
    /// Everything that runs outside a test: the API, the data layer and the seeding tool.
    /// </summary>
    /// <remarks>
    /// Reached through a type in each rather than by globbing an output directory, so
    /// adding a project without adding it here is at least a visible omission rather than
    /// a silently unscanned assembly.
    /// </remarks>
    public static TheoryData<string> ShippedAssemblies()
    {
        // Built by Add rather than a collection expression: the latter trips CA1825,
        // which is an error here.
        var assemblies = new TheoryData<string>();

        assemblies.Add(typeof(Program).Assembly.Location);
        assemblies.Add(typeof(AppDbContext).Assembly.Location);
        assemblies.Add(typeof(Pos.Seed.SeedOptions).Assembly.Location);

        return assemblies;
    }

    [Theory]
    [MemberData(nameof(ShippedAssemblies))]
    public void No_shipped_assembly_applies_a_migration(string assemblyPath)
    {
        Assert.True(
            File.Exists(assemblyPath),
            $"{assemblyPath} was not built, so nothing was scanned. A guard that quietly " +
            "checks nothing is worse than no guard: build the solution and run this again.");

        using var assembly = AssemblyDefinition.ReadAssembly(assemblyPath);

        var offenders = assembly.MainModule.Types
            .SelectMany(AllTypes)
            .SelectMany(type => type.Methods)
            .Where(method => method.HasBody)
            .SelectMany(method => method.Body.Instructions
                .Where(instruction =>
                    instruction.OpCode.Code is Code.Call or Code.Callvirt
                    && instruction.Operand is MethodReference)
                .Select(instruction => (Method: method, Called: (MethodReference)instruction.Operand)))
            .Where(call =>
                ForbiddenMembers.Contains(call.Called.Name, StringComparer.Ordinal)
                && DatabaseFacadeTypes.Contains(call.Called.DeclaringType.FullName, StringComparer.Ordinal))
            .Select(call =>
                $"{call.Method.DeclaringType.FullName}.{call.Method.Name} calls "
                + $"{call.Called.DeclaringType.FullName}::{call.Called.Name}")
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            $"{Path.GetFileName(assemblyPath)} applies migrations at run time:"
            + Environment.NewLine
            + string.Join(Environment.NewLine, offenders)
            + Environment.NewLine
            + "Migrations are an explicit, gated deploy step — see docs/phases/PHASE-8-deployment.md "
            + "§8.3. Two instances starting together race on the same migration and leave the "
            + "schema in a state neither expected, which cannot happen on the one developer "
            + "machine where this always gets added. The fix is the pipeline step, not this test.");
    }

    /// <summary>
    /// A type and everything nested in it. A call inside a lambda, an iterator or an async
    /// method lives in a compiler-generated nested type, not in the method that wrote it —
    /// and <c>await db.Database.MigrateAsync()</c> is exactly that shape.
    /// </summary>
    private static IEnumerable<TypeDefinition> AllTypes(TypeDefinition type) =>
        new[] { type }.Concat(type.NestedTypes.SelectMany(AllTypes));
}
