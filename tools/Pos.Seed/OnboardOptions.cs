using System.Globalization;
using System.Security.Cryptography;
using Pos.Core.Entities;

namespace Pos.Seed;

/// <summary>
/// What to create for a real, paying shop. Parsed from the command line.
/// </summary>
/// <remarks>
/// The same tool as <see cref="SeedOptions"/> and deliberately not the same defaults.
/// Every convenience that makes the dev seeder pleasant is a hazard here:
/// <list type="bullet">
/// <item>the dev seeder falls back to a hard-coded local connection string; this
/// <b>requires</b> one, because a mistyped invocation that silently onboards a real
/// customer into a laptop's Docker container is a very quiet failure</item>
/// <item>the dev seeder prints a fixed password; this generates one and shows it once</item>
/// <item>the dev seeder is safe to re-run; this <b>refuses</b> an existing slug, because
/// onboarding the same shop twice is a mistake and not an idempotent operation</item>
/// <item>the dev seeder never sets <see cref="TaxMode"/> or the business-day offset; this
/// requires both, because they are effectively immutable once trading starts</item>
/// </list>
/// <para>
/// It is a CLI holding a database credential, which is what makes "cannot be invoked with a
/// tenant token" true by construction: there is no endpoint, and a tenant's JWT opens
/// nothing here. See DECISIONS.md — no platform admin UI until after the first paying
/// client.
/// </para>
/// </remarks>
public sealed record OnboardOptions
{
    /// <summary>The verb that selects this mode.</summary>
    public const string Verb = "onboard";

    /// <summary>Read when <c>--connection</c> is absent. There is no built-in fallback.</summary>
    public const string ConnectionEnvironmentVariable = "POS_SEED_CONNECTION";

    /// <summary>
    /// Supplies the Owner's password instead of generating one.
    /// </summary>
    /// <remarks>
    /// An environment variable rather than a flag, so the password does not land in the
    /// shell history or in the process list, where <c>ps</c> shows every argument to every
    /// user on the machine.
    /// </remarks>
    public const string PasswordEnvironmentVariable = "POS_ONBOARD_PASSWORD";

    public required string ConnectionString { get; init; }

    public required string Slug { get; init; }

    public required string TenantName { get; init; }

    public required string CurrencyCode { get; init; }

    public required string TimeZoneId { get; init; }

    /// <summary>
    /// Inclusive or exclusive tax. <b>Effectively immutable once trading starts.</b>
    /// </summary>
    /// <remarks>
    /// Required rather than defaulted, because the default would be right for one country
    /// and silently wrong for the next — and by the time anyone notices, the shop has sales
    /// whose totals were computed the other way. <c>PUT /settings</c> refuses to change it
    /// once a sale exists (Phase 7.4), so this is the only moment it can be chosen.
    /// </remarks>
    public required TaxMode TaxMode { get; init; }

    /// <summary>
    /// When the trading day starts, as an offset from midnight in the tenant's zone.
    /// </summary>
    /// <remarks>
    /// Required for the same reason. A shop trading past midnight wants 04:00 so a 02:00
    /// shift close lands on the right day; changing it later moves every trading-day
    /// boundary that has already been reported on, so yesterday's Z-report stops matching
    /// yesterday. It is deliberately absent from <c>PUT /settings</c>.
    /// </remarks>
    public required TimeSpan BusinessDayStartOffset { get; init; }

    public required string OwnerEmail { get; init; }

    public required string OwnerName { get; init; }

    /// <summary>Generated unless <c>POS_ONBOARD_PASSWORD</c> was set. Shown once.</summary>
    public required string OwnerPassword { get; init; }

    /// <summary>True when this run generated the password, so the output can say so.</summary>
    public required bool PasswordWasGenerated { get; init; }

    /// <summary>Create and enrol a first till, printing its device token once.</summary>
    public required bool WithRegister { get; init; }

    public string? AddressLine { get; init; }

    public string? TaxNumber { get; init; }

    public string? ReceiptHeader { get; init; }

    public string? ReceiptFooter { get; init; }

    public static string Usage => """
        Creates a tenant and its first Owner. For a real shop — see `--help` without the
        verb for the development seeder.

        Usage:
          dotnet run --project tools/Pos.Seed -- onboard --connection <string> \
            --slug <string> --name <string> --tax-mode <Inclusive|Exclusive> [options]

        Required:
          --connection <string>    Postgres connection string, as the application role.
                                   Or set $POS_SEED_CONNECTION. There is no default: a
                                   silent fallback to a local database would onboard a
                                   real customer into somebody's laptop.
          --slug <string>          Typed at login. Must not already exist.
          --name <string>          Shop display name.
          --tax-mode <mode>        Inclusive or Exclusive. IMMUTABLE once trading starts.

        Recommended:
          --owner-email <address>  The first Owner. Default: owner@<slug>.example
          --owner-name <name>      Their display name. Default: Owner
          --currency <code>        ISO 4217. Default: EUR
          --timezone <iana>        IANA zone, e.g. Europe/Dublin. Default: Europe/Dublin
          --business-day-start <hh:mm>
                                   When the trading day starts. Default: 00:00. Use 04:00
                                   for a shop trading past midnight. Changing it later
                                   moves boundaries already reported on.

        Optional:
          --address <text>         Printed on receipts. Often legally required.
          --tax-number <text>      Printed on receipts. Often legally required.
          --receipt-header <text>
          --receipt-footer <text>
          --with-register          Also create and enrol a till, printing its device token.

        The Owner's password is generated and printed ONCE unless $POS_ONBOARD_PASSWORD is
        set. Pass it by environment and not by flag: an argument is visible in the shell
        history and to `ps`.
        """;

    /// <summary>True if <paramref name="args"/> selects onboarding rather than dev seeding.</summary>
    public static bool IsOnboarding(string[] args) =>
        args is [Verb, ..];

    public static OnboardOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var withRegister = false;

        // Skip the verb itself.
        for (var i = 1; i < args.Length; i++)
        {
            var argument = args[i];

            if (string.Equals(argument, "--with-register", StringComparison.Ordinal))
            {
                withRegister = true;
                continue;
            }

            if (!argument.StartsWith("--", StringComparison.Ordinal))
            {
                throw new SeedException($"Unexpected argument '{argument}'.");
            }

            var name = argument[2..];

            if (!ValueOptions.Contains(name))
            {
                throw new SeedException($"Unknown option '{argument}'. Try `onboard --help`.");
            }

            if (i + 1 >= args.Length)
            {
                throw new SeedException($"Option '{argument}' needs a value.");
            }

            values[name] = args[++i];
        }

        var connection = values.TryGetValue("connection", out var supplied) && supplied.Length > 0
            ? supplied
            : Environment.GetEnvironmentVariable(ConnectionEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(connection))
        {
            throw new SeedException(
                "--connection is required (or set $" + ConnectionEnvironmentVariable + "). " +
                "Unlike the development seeder there is no default, because a silent fallback " +
                "to a local database would onboard a real customer into a laptop.");
        }

        var slug = Tenant.NormalizeSlug(Required(values, "slug"))
            ?? throw new SeedException(
                "--slug contains no usable characters. Slugs are lowercase letters, digits and hyphens.");

        var environmentPassword = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        var generated = string.IsNullOrEmpty(environmentPassword);

        return new OnboardOptions
        {
            ConnectionString = connection,
            Slug = slug,
            TenantName = Required(values, "name"),
            CurrencyCode = Currency(Optional(values, "currency", "EUR")),
            TimeZoneId = TimeZone(Optional(values, "timezone", "Europe/Dublin")),
            TaxMode = Mode(Required(values, "tax-mode")),
            BusinessDayStartOffset = DayStart(Optional(values, "business-day-start", "00:00")),
            OwnerEmail = Optional(values, "owner-email", $"owner@{slug}.example"),
            OwnerName = Optional(values, "owner-name", "Owner"),
            OwnerPassword = generated ? GeneratePassword() : environmentPassword!,
            PasswordWasGenerated = generated,
            WithRegister = withRegister,
            AddressLine = values.GetValueOrDefault("address"),
            TaxNumber = values.GetValueOrDefault("tax-number"),
            ReceiptHeader = values.GetValueOrDefault("receipt-header"),
            ReceiptFooter = Text(values.GetValueOrDefault("receipt-footer"), "--receipt-footer"),
        };
    }

    private static readonly HashSet<string> ValueOptions = new(StringComparer.Ordinal)
    {
        "connection", "slug", "name", "currency", "timezone", "tax-mode", "business-day-start",
        "owner-email", "owner-name", "address", "tax-number", "receipt-header", "receipt-footer",
    };

    private static string Required(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out var value) && value.Length > 0
            ? value
            : throw new SeedException($"--{name} is required. Try `onboard --help`.");

    private static string Optional(Dictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out var value) && value.Length > 0 ? value : fallback;

    private static string Currency(string candidate) =>
        candidate.Length == 3 && candidate.All(char.IsAsciiLetter)
            ? candidate.ToUpperInvariant()
            : throw new SeedException("--currency must be a 3-letter ISO 4217 code, e.g. EUR.");

    /// <summary>
    /// Rejects a zone the runtime cannot resolve, here rather than at the first receipt.
    /// </summary>
    /// <remarks>
    /// This is the check that would have caught the Phase 6.1 globalization defect from the
    /// outside: under <c>InvariantGlobalization</c> an IANA id is unresolvable and every
    /// business-day boundary and receipt timestamp fails — but only at render time, weeks
    /// later, on somebody else's machine.
    /// </remarks>
    private static string TimeZone(string candidate)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(candidate).Id;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new SeedException(
                $"--timezone '{candidate}' is not a time zone this machine can resolve. " +
                "Use an IANA id such as Europe/Dublin.");
        }
    }

    private static TaxMode Mode(string candidate) =>
        Enum.TryParse<TaxMode>(candidate, ignoreCase: true, out var mode)
            ? mode
            : throw new SeedException(
                $"--tax-mode must be Inclusive or Exclusive, not '{candidate}'. " +
                "Inclusive means the shelf price already contains the tax, which is usual " +
                "for retail in the EU and the UK. It cannot be changed once the shop has sales.");

    private static TimeSpan DayStart(string candidate)
    {
        if (!TimeSpan.TryParseExact(candidate, @"hh\:mm", CultureInfo.InvariantCulture, out var offset)
            || offset < TimeSpan.Zero
            || offset >= TimeSpan.FromHours(24))
        {
            throw new SeedException(
                "--business-day-start must be hh:mm between 00:00 and 23:59, e.g. 04:00.");
        }

        return offset;
    }

    private static string? Text(string? candidate, string option)
    {
        if (candidate is not null && candidate.Length > Tenant.ReceiptTextMaxLength)
        {
            throw new SeedException(
                $"{option} must be at most {Tenant.ReceiptTextMaxLength} characters.");
        }

        return candidate;
    }

    /// <summary>
    /// A password the Owner will change, generated so nobody has to invent one.
    /// </summary>
    /// <remarks>
    /// Built from an unambiguous alphabet — no <c>0</c>/<c>O</c> or <c>1</c>/<c>l</c> —
    /// because this gets read down a telephone at least once, and a password that cannot be
    /// dictated reliably gets replaced by one somebody chose instead.
    /// <para>
    /// Composed to satisfy <c>AddPosIdentity</c>'s rules (10+, upper, lower, digit) by
    /// construction rather than by retrying until it happens to pass.
    /// </para>
    /// </remarks>
    public static string GeneratePassword()
    {
        const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string Lower = "abcdefghijkmnopqrstuvwxyz";
        const string Digits = "23456789";

        // Three groups of four, hyphenated: readable aloud, and comfortably past the
        // ten-character minimum.
        return string.Join('-', Enumerable.Range(0, 3).Select(_ => new string(
        [
            Pick(Upper),
            Pick(Lower),
            Pick(Lower),
            Pick(Digits),
        ])));

        static char Pick(string alphabet) => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
    }
}
