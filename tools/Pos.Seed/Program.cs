using Microsoft.Extensions.DependencyInjection;
using Pos.Data;
using Pos.Data.Identity;
using Pos.Seed;

// Development provisioning, run by hand:
//
//   dotnet run --project tools/Pos.Seed
//
// There is no onboarding endpoint by decision, so this is how a migrated database gets a
// tenant to log into. See DevSeeder for what it writes and why it writes it that way.

if (SeedOptions.WantsHelp(args))
{
    Console.WriteLine(SeedOptions.Usage);
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
