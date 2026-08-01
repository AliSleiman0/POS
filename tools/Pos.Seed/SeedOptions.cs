using Pos.Core.Entities;

namespace Pos.Seed;

/// <summary>What to seed, and where. Parsed from the command line.</summary>
/// <remarks>
/// Every value has a working default, because the common case is "give me a shop to log
/// into" and a tool that demands eight arguments for that gets wrapped in a script nobody
/// maintains. The defaults are the local Docker database from <c>docker-compose.yml</c>
/// and credentials that are obvious throwaways on the same terms as everything else in it.
/// </remarks>
public sealed record SeedOptions
{
    /// <summary>
    /// The local dev database, connected as <c>pos_app</c> — the same role the API uses.
    /// </summary>
    /// <remarks>
    /// Deliberately not the owner account. Seeding as the owner would bypass row-level
    /// security (it is a superuser locally), so a policy that rejected these writes would
    /// still let the seed succeed and the mistake would surface later as an empty list in
    /// the UI. If this tool can write it, the running API can read it.
    /// </remarks>
    public const string DefaultConnectionString =
        "Host=localhost;Port=5432;Database=pos_dev;Username=pos_app;Password=dev_only_not_a_secret";

    /// <summary>Checked when <c>--connection</c> is absent, so CI or a remote host can supply one.</summary>
    public const string ConnectionEnvironmentVariable = "POS_SEED_CONNECTION";

    /// <summary>Satisfies the Identity password rules in <c>AddPosIdentity</c>: 10+, upper, lower, digit.</summary>
    public const string DefaultPassword = "Dev-Password-1";

    public const string DefaultSlug = "corner-shop";
    public const string DefaultTenantName = "Corner Shop";
    public const string DefaultCashierPin = "4821";
    public const string DefaultManagerPin = "7391";

    public required string ConnectionString { get; init; }

    /// <summary>Already normalised through <see cref="Tenant.NormalizeSlug"/>.</summary>
    public required string Slug { get; init; }

    public required string TenantName { get; init; }

    public required string CurrencyCode { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>Shared by all three seeded users. Dev only, and printed on completion.</summary>
    public required string Password { get; init; }

    public required string CashierPin { get; init; }

    public required string ManagerPin { get; init; }

    /// <summary>
    /// Reissue the enrolled register's device token.
    /// </summary>
    /// <remarks>
    /// Off by default because a device token is shown once and then only exists as a
    /// SHA-256 — so a re-run cannot reprint the old one, and silently minting a new one
    /// every time would log out the till you are in the middle of testing with.
    /// </remarks>
    public required bool RotateDeviceToken { get; init; }

    /// <summary>
    /// Seed the tenant and its staff but no catalog, for testing what an empty shop looks
    /// like — the state a real tenant is in on its first day, and the one an empty-list
    /// screen is easiest to get wrong in.
    /// </summary>
    public required bool SkipCatalog { get; init; }

    public static string Usage => """
        Seeds the local development database with a tenant, three users and two registers.

        Usage:
          dotnet run --project tools/Pos.Seed -- [options]

        Options:
          --connection <string>    Postgres connection string.
                                   Default: the local pos_app connection, or $POS_SEED_CONNECTION.
          --slug <string>          Tenant slug, typed at login. Default: corner-shop
          --name <string>          Tenant display name. Default: Corner Shop
          --currency <string>      ISO 4217 code. Default: EUR
          --timezone <string>      IANA time zone. Default: Europe/Dublin
          --password <string>      Password for all three users. Default: Dev-Password-1
          --cashier-pin <digits>   4-6 digits. Default: 4821
          --manager-pin <digits>   4-6 digits. Default: 7391
          --rotate-device-token    Reissue the front register's device token and print it.
                                   A token is only shown at enrollment, so a re-run cannot
                                   reprint the previous one.
          --no-catalog             Seed the tenant and its staff but no products.
          -h, --help               This text.

        Re-running is safe: anything that already exists is left alone.
        """;

    public static bool WantsHelp(string[] args) =>
        args is not null && args.Any(arg =>
            string.Equals(arg, "--help", StringComparison.Ordinal) ||
            string.Equals(arg, "-h", StringComparison.Ordinal));

    /// <summary>Parses <paramref name="args"/>, throwing <see cref="SeedException"/> on bad input.</summary>
    public static SeedOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var rotate = false;
        var skipCatalog = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];

            if (string.Equals(argument, "--rotate-device-token", StringComparison.Ordinal))
            {
                rotate = true;
                continue;
            }

            if (string.Equals(argument, "--no-catalog", StringComparison.Ordinal))
            {
                skipCatalog = true;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new SeedException($"Unexpected argument '{argument}'.");
            }

            var name = argument[2..];

            if (!ValueOptions.Contains(name))
            {
                throw new SeedException($"Unknown option '{argument}'.");
            }

            if (i + 1 >= args.Length)
            {
                throw new SeedException($"Option '{argument}' needs a value.");
            }

            values[name] = args[++i];
        }

        var slug = Tenant.NormalizeSlug(Value(values, "slug", DefaultSlug))
            ?? throw new SeedException("The slug contains no usable characters. Slugs are lowercase letters, digits and hyphens.");

        return new SeedOptions
        {
            ConnectionString = Value(
                values,
                "connection",
                Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable) is { Length: > 0 } fromEnvironment
                    ? fromEnvironment
                    : DefaultConnectionString),
            Slug = slug,
            TenantName = Value(values, "name", DefaultTenantName),
            CurrencyCode = Value(values, "currency", "EUR"),
            TimeZoneId = Value(values, "timezone", "Europe/Dublin"),
            Password = Value(values, "password", DefaultPassword),
            CashierPin = Pin(Value(values, "cashier-pin", DefaultCashierPin), "--cashier-pin"),
            ManagerPin = Pin(Value(values, "manager-pin", DefaultManagerPin), "--manager-pin"),
            RotateDeviceToken = rotate,
            SkipCatalog = skipCatalog,
        };
    }

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "connection", "slug", "name", "currency", "timezone", "password", "cashier-pin", "manager-pin",
    };

    private static string Value(Dictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

    /// <summary>
    /// The same 4-6 digit rule the PIN endpoint enforces, checked here so a bad PIN fails
    /// before any row is written rather than after two of the three users exist.
    /// </summary>
    private static string Pin(string candidate, string option)
    {
        if (candidate.Length is < 4 or > 6 || !candidate.All(char.IsAsciiDigit))
        {
            throw new SeedException($"{option} must be 4 to 6 digits.");
        }

        return candidate;
    }
}
