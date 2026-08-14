using System.Reflection;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Seed;

namespace Pos.Api.Tests.Common;

/// <summary>
/// The onboarding command's refusals, and the thing it must not be reachable through.
/// </summary>
/// <remarks>
/// Parsing is where every onboarding safeguard lives, and it is pure — so it can be tested
/// exhaustively without a database. What the command then writes is exercised end to end in
/// Phase 8.8's verification against the deployed instance.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class OnboardingTests(PosApiFactory factory)
{
    private static string[] Minimal(params string[] extra) =>
    [
        "onboard",
        "--connection", "Host=db;Database=pos;Username=pos_app;Password=x",
        "--slug", "harbour-stores",
        "--name", "Harbour Stores",
        "--tax-mode", "Inclusive",
        .. extra,
    ];

    [Fact]
    public void The_verb_selects_onboarding_and_nothing_else_does()
    {
        Assert.True(OnboardOptions.IsOnboarding(["onboard", "--slug", "x"]));

        // The bare command must stay the development seeder. A tool where the dangerous
        // mode is the default is a tool that eventually runs the dangerous mode by accident.
        Assert.False(OnboardOptions.IsOnboarding([]));
        Assert.False(OnboardOptions.IsOnboarding(["--slug", "onboard"]));
    }

    [Fact]
    public void A_connection_string_is_required_with_no_local_fallback()
    {
        var previous = Environment.GetEnvironmentVariable(OnboardOptions.ConnectionEnvironmentVariable);
        Environment.SetEnvironmentVariable(OnboardOptions.ConnectionEnvironmentVariable, null);

        try
        {
            var failure = Assert.Throws<SeedException>(() => OnboardOptions.Parse(
                ["onboard", "--slug", "x", "--name", "X", "--tax-mode", "Inclusive"]));

            // The development seeder falls back to the local Docker database. Inheriting
            // that here means a mistyped command silently onboards a paying customer into
            // somebody's laptop, and the failure is not visible until they cannot log in.
            Assert.Contains("--connection is required", failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OnboardOptions.ConnectionEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void The_tax_mode_must_be_stated()
    {
        // Not defaulted. A default is right for one country and silently wrong for the
        // next, and by the time anybody notices the shop has sales priced the other way —
        // at which point PUT /settings refuses to change it, correctly.
        var failure = Assert.Throws<SeedException>(() => OnboardOptions.Parse(
            ["onboard", "--connection", "x", "--slug", "x", "--name", "X"]));

        Assert.Contains("--tax-mode is required", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Inclusive", TaxMode.Inclusive)]
    [InlineData("exclusive", TaxMode.Exclusive)]
    public void A_stated_tax_mode_is_taken(string given, TaxMode expected) =>
        Assert.Equal(expected, OnboardOptions.Parse(Minimal("--tax-mode", given)).TaxMode);

    [Fact]
    public void A_nonsense_tax_mode_says_what_the_choice_means()
    {
        var failure = Assert.Throws<SeedException>(() => OnboardOptions.Parse(Minimal("--tax-mode", "Sideways")));

        // The message has to teach, not just refuse: whoever runs this is onboarding their
        // first customer and may not know which answer is right for that country.
        Assert.Contains("shelf price already contains the tax", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unresolvable_time_zone_is_refused_at_the_command_and_not_at_the_first_receipt()
    {
        var failure = Assert.Throws<SeedException>(() => OnboardOptions.Parse(Minimal("--timezone", "Middle/Earth")));

        // This is the check that catches a globalization regression from the outside. Under
        // InvariantGlobalization an IANA id is unresolvable and every business-day boundary
        // and receipt timestamp fails — but only at render time, weeks later.
        Assert.Contains("not a time zone", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("04:00")]
    [InlineData("00:00")]
    [InlineData("23:59")]
    public void A_business_day_offset_inside_a_day_is_accepted(string given) =>
        Assert.Equal(
            TimeSpan.Parse(given, System.Globalization.CultureInfo.InvariantCulture),
            OnboardOptions.Parse(Minimal("--business-day-start", given)).BusinessDayStartOffset);

    [Theory]
    [InlineData("25:00")]
    [InlineData("-01:00")]
    [InlineData("four")]
    public void A_business_day_offset_outside_a_day_is_refused(string given) =>
        Assert.Throws<SeedException>(() => OnboardOptions.Parse(Minimal("--business-day-start", given)));

    [Fact]
    public void A_password_is_generated_when_the_environment_does_not_supply_one()
    {
        var previous = Environment.GetEnvironmentVariable(OnboardOptions.PasswordEnvironmentVariable);
        Environment.SetEnvironmentVariable(OnboardOptions.PasswordEnvironmentVariable, null);

        try
        {
            var options = OnboardOptions.Parse(Minimal());

            Assert.True(options.PasswordWasGenerated);
            Assert.NotEqual(SeedOptions.DefaultPassword, options.OwnerPassword);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OnboardOptions.PasswordEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void A_generated_password_satisfies_the_identity_rules_by_construction()
    {
        // AddPosIdentity requires 10+ characters with an upper, a lower and a digit.
        // Generating until it happens to pass would work almost always and hang rarely;
        // composing it from one of each cannot fail.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var password = OnboardOptions.GeneratePassword();

            Assert.True(password.Length >= 10, $"'{password}' is shorter than the minimum.");
            Assert.Contains(password, char.IsAsciiLetterUpper);
            Assert.Contains(password, char.IsAsciiLetterLower);
            Assert.Contains(password, char.IsAsciiDigit);

            // Read down a telephone at least once. 0/O and 1/l are the pairs that get
            // transcribed wrongly, and a password that cannot be dictated reliably gets
            // replaced by one somebody chose instead.
            Assert.DoesNotContain('0', password);
            Assert.DoesNotContain('O', password);
            Assert.DoesNotContain('1', password);
            Assert.DoesNotContain('l', password);
            Assert.DoesNotContain('I', password);
        }
    }

    [Fact]
    public void Two_generated_passwords_differ()
    {
        // Guards against a constant sneaking in behind the composition rules above, which
        // every other assertion here would still pass.
        var passwords = Enumerable.Range(0, 50).Select(_ => OnboardOptions.GeneratePassword()).ToHashSet(StringComparer.Ordinal);

        Assert.True(passwords.Count > 45, "Generated passwords repeat far more than chance allows.");
    }

    [Fact]
    public void An_environment_password_is_used_and_reported_as_not_generated()
    {
        var previous = Environment.GetEnvironmentVariable(OnboardOptions.PasswordEnvironmentVariable);
        Environment.SetEnvironmentVariable(OnboardOptions.PasswordEnvironmentVariable, "Chosen-Password-9");

        try
        {
            var options = OnboardOptions.Parse(Minimal());

            Assert.Equal("Chosen-Password-9", options.OwnerPassword);

            // So the output does not tell an operator to write down a password they chose
            // and already have.
            Assert.False(options.PasswordWasGenerated);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OnboardOptions.PasswordEnvironmentVariable, previous);
        }
    }

    /// <summary>
    /// Onboarding is a command holding a database credential, and there is no way to reach
    /// it with a tenant's token — because there is no route that creates a tenant at all.
    /// </summary>
    /// <remarks>
    /// The phase's exit criterion is "it cannot be invoked with a tenant token". That is
    /// true by construction today; this is what notices if somebody adds the convenient
    /// self-service signup endpoint that would make it false. Such an endpoint would be
    /// anonymous by necessity — a caller has no tenant yet — which makes it the one route
    /// in the API with no tenant boundary behind it.
    /// </remarks>
    [Fact]
    public void No_route_creates_a_tenant()
    {
        var routes = factory.Services
            .GetRequiredService<EndpointDataSource>()
            .Endpoints
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(routes, route =>
            route.Contains("/tenants", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/signup", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/register-shop", StringComparison.OrdinalIgnoreCase)
            || route.Contains("/onboard", StringComparison.OrdinalIgnoreCase));
    }
}
