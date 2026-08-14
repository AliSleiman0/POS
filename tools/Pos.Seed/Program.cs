using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Pos.Data;
using Pos.Data.Identity;
using Pos.Seed;

// Two commands in one tool, and deliberately one tool:
//
//   dotnet run --project tools/Pos.Seed                    development seeding
//   dotnet run --project tools/Pos.Seed -- onboard ...     a real shop
//
// A second tool would drift from this one — same entities, same Identity rules, same
// interceptors, and two places to remember when any of them change. What differs is
// everything that makes the dev seeder convenient and would make onboarding dangerous: a
// default connection string, a printed fixed password, and re-runs that quietly continue.
// See docs/phases/PHASE-8-deployment.md §8.7 and docs/RUNBOOK.md.

if (OnboardOptions.IsOnboarding(args))
{
    return await OnboardAsync(args);
}

if (SeedOptions.WantsHelp(args))
{
    Console.WriteLine(SeedOptions.Usage);
    Console.WriteLine();
    Console.WriteLine("For a real shop rather than a dev database: `onboard --help`.");
    return 0;
}

try
{
    var options = SeedOptions.Parse(args);

    var services = new ServiceCollection();

    services.AddPosData(options.ConnectionString);
    services.AddPosIdentity();

    await using var provider = services.BuildServiceProvider();

    await new DevSeeder(provider, options).RunAsync();

    return 0;
}
catch (SeedException ex)
{
    // Expected, actionable failures print as a message. Anything else is a bug and is
    // allowed to come out as an unhandled exception with its stack trace intact.
    await Console.Error.WriteLineAsync(ex.Message);

    return 1;
}

static async Task<int> OnboardAsync(string[] args)
{
    if (SeedOptions.WantsHelp(args))
    {
        Console.WriteLine(OnboardOptions.Usage);
        return 0;
    }

    try
    {
        var options = OnboardOptions.Parse(args);

        var services = new ServiceCollection();

        services.AddPosData(options.ConnectionString);
        services.AddPosIdentity();

        await using var provider = services.BuildServiceProvider();

        Report(await new Onboarder(provider, options).RunAsync(), options);

        return 0;
    }
    catch (SeedException ex)
    {
        await Console.Error.WriteLineAsync(ex.Message);

        return 1;
    }
}

/// <summary>
/// Everything the operator has to capture before this window is closed.
/// </summary>
/// <remarks>
/// Written to stdout in one block and never again. The password is not stored anywhere in
/// recoverable form and there is no password-reset flow yet, so losing it means the runbook's
/// "reset a locked-out Owner" procedure — see docs/RUNBOOK.md.
/// </remarks>
static void Report(OnboardResult result, OnboardOptions options)
{
    var writer = Console.Out;

    writer.WriteLine();
    writer.WriteLine($"Onboarded  {result.Slug}  ({options.TenantName})");
    writer.WriteLine($"           tenant id   {result.TenantId.ToString("D", CultureInfo.InvariantCulture)}");
    writer.WriteLine($"           currency    {options.CurrencyCode}");
    writer.WriteLine($"           time zone   {options.TimeZoneId}");
    writer.WriteLine($"           tax mode    {options.TaxMode}  (cannot be changed once the shop has sales)");
    writer.WriteLine($"           day starts  {options.BusinessDayStartOffset:hh\\:mm}  (cannot be changed at all)");
    writer.WriteLine();
    writer.WriteLine("Owner");
    writer.WriteLine($"           email       {result.OwnerEmail}");
    writer.WriteLine($"           id          {result.OwnerId.ToString("D", CultureInfo.InvariantCulture)}");
    writer.WriteLine($"           password    {result.Password}");

    if (result.PasswordWasGenerated)
    {
        writer.WriteLine();
        writer.WriteLine("  This password is shown ONCE and is not recoverable. Hand it over through a");
        writer.WriteLine("  channel you would be willing to send a key to, and note that there is no");
        writer.WriteLine("  self-service password change yet — see docs/RUNBOOK.md.");
    }

    if (result.DeviceToken is { } token)
    {
        writer.WriteLine();
        writer.WriteLine("Register   Front Counter");
        writer.WriteLine($"           id          {result.RegisterId!.Value.ToString("D", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"           device token {token}");
        writer.WriteLine();
        writer.WriteLine("  Also shown ONCE — only its SHA-256 is stored. A till that loses it must be");
        writer.WriteLine("  re-enrolled from /admin/tills.");
    }

    writer.WriteLine();
    writer.WriteLine("Next: sign in at the web app with the slug, email and password above, then add");
    writer.WriteLine("staff at /admin/employees and the catalog at /admin/products.");
}
