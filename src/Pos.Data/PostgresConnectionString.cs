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
            builder[key] = parameterValue;
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
}
