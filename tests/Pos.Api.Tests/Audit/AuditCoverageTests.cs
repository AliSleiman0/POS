using System.Reflection;
using Pos.Core.Entities;

namespace Pos.Api.Tests.Audit;

/// <summary>
/// The guard that outlives this phase: every audited action is accounted for.
/// </summary>
/// <remarks>
/// The sibling of <c>EndpointCoverageTests</c>, and it exists for the same reason. An audit
/// log fails silently — an action that stops writing its entry leaves a green suite and a log
/// that is missing exactly what somebody went looking for — so the thing to enforce is not
/// "these tests pass" but "nothing is unaccounted for".
/// </remarks>
public sealed class AuditCoverageTests
{
    [Fact]
    public void Every_audited_action_has_a_row()
    {
        var declared = Enum.GetNames<AuditAction>().ToHashSet(StringComparer.Ordinal);
        var covered = AuditManifest.Cases.Select(c => c.Action.ToString()).ToHashSet(StringComparer.Ordinal);

        var unaccounted = declared.Except(covered, StringComparer.Ordinal).Order().ToArray();

        Assert.True(
            unaccounted.Length == 0,
            "AuditAction members with no row in AuditManifest: " + string.Join(", ", unaccounted)
            + ". Add the row and the test it names — an action nobody proves is written is an "
            + "action that will one day stop being written without anything going red.");

        // Both directions. A stale row for a member that has been removed is a claim about
        // behaviour that no longer exists, which is how a manifest stops being read.
        var stale = covered.Except(declared, StringComparer.Ordinal).Order().ToArray();

        Assert.True(
            stale.Length == 0,
            "AuditManifest rows for actions that no longer exist: " + string.Join(", ", stale));
    }

    [Fact]
    public void Every_row_names_a_test_that_exists()
    {
        var tests = typeof(AuditCoverageTests).Assembly
            .GetTypes()
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            .Where(method =>
                method.GetCustomAttributes().Any(a =>
                    a.GetType().Name is "FactAttribute" or "TheoryAttribute"))
            .Select(method => $"{method.DeclaringType!.Name}.{method.Name}")
            .ToHashSet(StringComparer.Ordinal);

        // Resolved by reflection rather than by a compiler reference, deliberately: a `nameof`
        // would be satisfied by pointing at any test at all, including one that asserts
        // nothing about this action. A name that has to resolve to a real method at least
        // fails loudly when the test it points at is renamed or deleted.
        var missing = AuditManifest.Cases
            .Where(c => !tests.Contains(c.CoveredBy))
            .Select(c => $"{c.Action} -> {c.CoveredBy}")
            .Order()
            .ToArray();

        Assert.True(
            missing.Length == 0,
            "AuditManifest rows naming a test that does not exist: " + string.Join(", ", missing));
    }

    [Fact]
    public void Every_row_says_where_the_entry_is_written()
    {
        var vague = AuditManifest.Cases
            .Where(c => string.IsNullOrWhiteSpace(c.WrittenBy))
            .Select(c => c.Action.ToString())
            .ToArray();

        Assert.True(
            vague.Length == 0,
            "AuditManifest rows with no WrittenBy: " + string.Join(", ", vague));
    }
}
