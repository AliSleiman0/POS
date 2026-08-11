using Npgsql;

namespace Pos.Data;

/// <summary>
/// Accepts a Postgres connection string in either shape a host might hand us.
/// </summary>
/// <remarks>
/// Npgsql speaks keyword form — <c>Host=…;Database=…;Username=…;Password=…</c> — and most
/// managed platforms hand out a URI instead: <c>postgres://user:pass@host:5432/db</c>. The
/// two are not interchangeable, and the failure is late and unhelpful: the configuration
/// looks present and correct, the app starts, and the first query throws a parse error about
/// a keyword called "postgres".
/// <para>
/// Converting here rather than asking an operator to rewrite it by hand, because the hand
/// version is done under time pressure, gets the password's special characters wrong, and is
/// the sort of step that ends up as a footnote in a runbook nobody reads.
/// </para>
/// </remarks>
public static class PostgresConnectionString
{
    /// <summary>The URI schemes a platform might use. Both mean the same thing.</summary>
    private static readonly string[] Schemes = ["postgres", "postgresql"];

    /// <summary>
    /// Returns <paramref name="value"/> in Npgsql keyword form, converting from a URI if
    /// that is what it is.
    /// </summary>
    /// <remarks>
    /// A value that is already keyword form is returned untouched rather than round-tripped
    /// through a builder — round-tripping would silently drop any keyword this method did
    /// not think to preserve, which is a large set and a bad way to lose <c>Pooling</c> or
    /// <c>Timeout</c>.
    /// </remarks>
    /// <exception cref="ArgumentException">The value is a URI but not a usable one.</exception>
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();

        if (!LooksLikeUri(trimmed))
        {
            return trimmed;
        }

        Uri uri;

        try
        {
            uri = new Uri(trimmed);
        }
        catch (UriFormatException ex)
        {
            throw new ArgumentException(
                "The Postgres connection string starts like a URI but could not be parsed. " +
                "Expected postgres://user:password@host:port/database.",
                nameof(value),
                ex);
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,

            // -1 is what Uri reports for "no port given". Postgres's own default is the
            // right answer then, rather than a literal -1 that fails at connect time.
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,

            // A leading slash always, and it is not part of the database name.
            Database = uri.AbsolutePath.TrimStart('/'),
        };

        var credentials = uri.UserInfo.Split(':', 2);

        if (credentials.Length > 0 && credentials[0].Length > 0)
        {
            // Percent-decoded: a password containing @ or / has to be encoded in a URI, and
            // handing the encoded form to Postgres authenticates with the wrong password —
            // which reads as "the credentials are wrong" rather than "the parsing is wrong".
            builder.Username = Uri.UnescapeDataString(credentials[0]);
        }

        if (credentials.Length > 1 && credentials[1].Length > 0)
        {
            builder.Password = Uri.UnescapeDataString(credentials[1]);
        }

        // Query parameters carry what matters operationally — sslmode above all, which
        // several managed hosts require and which appears nowhere else in the URI.
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=', StringComparison.Ordinal);

            if (separator <= 0)
            {
                continue;
            }

            var key = Uri.UnescapeDataString(pair[..separator]);
            var parameterValue = Uri.UnescapeDataString(pair[(separator + 1)..]);

            // The indexer throws on a keyword Npgsql does not know, which is the right
            // outcome: a misspelled sslmode that was silently dropped would leave the
            // connection unencrypted while the configuration looked deliberate.
            builder[TranslateKey(key)] = TranslateValue(key, parameterValue);
        }

        if (builder.Database.Length == 0)
        {
            throw new ArgumentException(
                "The Postgres connection URI names no database. Expected " +
                "postgres://user:password@host:port/database.",
                nameof(value));
        }

        return builder.ConnectionString;
    }

    private static bool LooksLikeUri(string value) =>
        Schemes.Any(scheme => value.StartsWith(scheme + "://", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Rewrites a libpq parameter <i>name</i> that Npgsql does not accept as an alias.
    /// </summary>
    /// <remarks>
    /// Npgsql aliases most of libpq's names — <c>sslmode</c>, <c>dbname</c>, <c>user</c> —
    /// but not all of them, and an unaliased one throws "Couldn't set …". Only the ones
    /// that actually differ are listed; everything else is passed through so Npgsql can
    /// accept or reject it on its own terms.
    /// </remarks>
    private static string TranslateKey(string key) => Canonical(key) switch
    {
        // Needed against a managed Postgres that terminates TLS at a proxy, where SCRAM
        // channel binding cannot succeed and the connection is impossible without this.
        "channelbinding" => "Channel Binding",

        _ => key,
    };

    /// <summary>
    /// Rewrites a libpq parameter <i>value</i> into the spelling Npgsql accepts.
    /// </summary>
    /// <remarks>
    /// A URI comes from a platform that speaks libpq, and libpq's value vocabulary is not
    /// Npgsql's: <c>sslmode=verify-full</c> is exactly the same intent as
    /// <c>SSL Mode=VerifyFull</c> and Npgsql rejects the former. Npgsql accepts libpq's
    /// parameter <i>names</i> as aliases, which is why this is about values only and why
    /// the simpler cases (<c>sslmode=require</c>) work without it.
    /// <para>
    /// Found the hard way: Render's own connection string carries <c>sslmode</c>, and a
    /// deployment that pastes it with <c>verify-full</c> — the setting you actually want,
    /// because it validates the certificate — failed to start with "Couldn't set sslmode".
    /// </para>
    /// <para>
    /// Only the hyphenated forms need translating. Anything else is passed through and
    /// left for Npgsql to accept or reject on its own terms, so this cannot quietly
    /// swallow a value it has not been taught about.
    /// </para>
    /// </remarks>
    private static string TranslateValue(string key, string value) =>
        (Canonical(key), Canonical(value)) switch
        {
            ("sslmode", "verifyca") => nameof(Npgsql.SslMode.VerifyCA),
            ("sslmode", "verifyfull") => nameof(Npgsql.SslMode.VerifyFull),

            // libpq spells these with a hyphen too, and Npgsql's enum does not.
            ("channelbinding", "disable") => "Disable",
            ("channelbinding", "prefer") => "Prefer",
            ("channelbinding", "require") => "Require",

            _ => value,
        };

    /// <summary>
    /// Lower-cased with separators removed, so <c>verify-full</c>, <c>VerifyFull</c> and
    /// <c>verify_full</c> all compare equal.
    /// </summary>
    private static string Canonical(string value) =>
        string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();
}
