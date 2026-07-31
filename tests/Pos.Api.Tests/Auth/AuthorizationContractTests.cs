using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Auth;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// Guards invariant 7: authorization is by named policy, every endpoint has one, and the
/// policy map matches the table people actually read.
/// </summary>
[Collection(PosApiCollection.Name)]
public sealed partial class AuthorizationContractTests(PosApiFactory factory)
{
    [Fact]
    public void Every_endpoint_states_its_own_authorization()
    {
        var endpoints = factory.Services.GetRequiredService<EndpointDataSource>().Endpoints;

        Assert.NotEmpty(endpoints);

        var unprotected = endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null
                     && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.DisplayName ?? "(unnamed)")
            .ToArray();

        // No FallbackPolicy exists on purpose. One would turn "nobody authorized this
        // endpoint" into "any authenticated user may call it" — a Cashier reaching an
        // Owner endpoint, silently. A missing decision should fail the build, not default.
        Assert.True(
            unprotected.Length == 0,
            "Endpoints with neither an authorization policy nor an explicit AllowAnonymous: "
            + string.Join(", ", unprotected));
    }

    [Fact]
    public void No_endpoint_tests_a_role_literal()
    {
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(SourceRoot(), "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            if (RoleLiteralAttribute().IsMatch(text) || RequireRoleCall().IsMatch(text))
            {
                offenders.Add(Path.GetFileName(file));
            }
        }

        // Role literals scattered across endpoints are how "can supervisors do refunds?"
        // becomes an audit instead of an edit — and how two endpoints that both meant
        // "a manager" end up disagreeing.
        Assert.True(
            offenders.Count == 0,
            "Role literals found outside the policy catalog in: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_policy_map_matches_the_table_in_ARCHITECTURE_md()
    {
        var documented = ReadPolicyTable();

        Assert.NotEmpty(documented);

        // The doc is what a person reads when deciding what a role may do; the catalog is
        // what the server enforces. Whichever of the two is wrong, them disagreeing is
        // worse than either.
        Assert.Equal(
            documented.Keys.Order(StringComparer.Ordinal),
            PolicyCatalog.RolesByPolicy.Keys.Order(StringComparer.Ordinal));

        foreach (var (policy, roles) in documented)
        {
            Assert.Equal(
                roles.Order(StringComparer.Ordinal),
                PolicyCatalog.RolesByPolicy[policy].Order(StringComparer.Ordinal));
        }
    }

    [Theory]
    [InlineData(RoleNames.Cashier, 1)]
    [InlineData(RoleNames.Manager, 7)]
    [InlineData(RoleNames.Owner, 9)]
    public void Each_role_grants_the_expected_number_of_policies(string role, int expected)
    {
        // A blunt count, deliberately: it fails when a policy is added without deciding who
        // gets it, which the per-policy assertions above would not catch on their own.
        Assert.Equal(expected, PolicyCatalog.PoliciesFor(role).Count);
    }

    [Fact]
    public void An_unknown_role_grants_nothing()
    {
        Assert.Empty(PolicyCatalog.PoliciesFor("Auditor"));
        Assert.Empty(PolicyCatalog.PoliciesFor(null));
    }

    /// <summary>Reads the policy/role grid out of the architecture document.</summary>
    private static Dictionary<string, List<string>> ReadPolicyTable()
    {
        var path = Path.Combine(RepositoryRoot(), "docs", "ARCHITECTURE.md");
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var columns = new[] { RoleNames.Cashier, RoleNames.Manager, RoleNames.Owner };

        foreach (var line in File.ReadLines(path))
        {
            var match = PolicyTableRow().Match(line);

            if (!match.Success)
            {
                continue;
            }

            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            var roles = new List<string>();

            // cells[0] is empty (the leading pipe), cells[1] is the policy name.
            for (var i = 0; i < columns.Length; i++)
            {
                if (cells[i + 2].Contains('✅', StringComparison.Ordinal))
                {
                    roles.Add(columns[i]);
                }
            }

            result[match.Groups["policy"].Value] = roles;
        }

        return result;
    }

    private static string SourceRoot() => Path.Combine(RepositoryRoot(), "src");

    private static string RepositoryRoot()
    {
        // Anchored on the test assembly's output directory, not [CallerFilePath]: CI builds
        // deterministically, and the compiler then bakes in a /_/ path that exists nowhere.
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Pos.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException(
            $"Could not locate the repository root (Pos.slnx) from '{AppContext.BaseDirectory}'.");
    }

    [GeneratedRegex(@"\[Authorize\s*\(\s*Roles\s*=")]
    private static partial Regex RoleLiteralAttribute();

    /// <summary>Catches <c>RequireRole(...)</c> anywhere except the catalog that defines the policies.</summary>
    [GeneratedRegex(@"RequireRole\s*\(\s*""")]
    private static partial Regex RequireRoleCall();

    [GeneratedRegex(@"^\|\s*`(?<policy>Can\w+)`\s*\|")]
    private static partial Regex PolicyTableRow();
}
