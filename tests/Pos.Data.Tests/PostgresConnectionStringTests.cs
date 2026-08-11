using Npgsql;
using Pos.Data;

namespace Pos.Data.Tests;

/// <summary>
/// Accepting either shape of Postgres connection string.
/// </summary>
/// <remarks>
/// The failure this prevents is late and misleading. A URI handed to Npgsql produces a
/// parse error about a keyword named "postgres", by which point the configuration looks
/// present and correct and the app has already started — so it reads as a code fault rather
/// than a configuration one.
/// <para>
/// Pure, so none of this needs a database.
/// </para>
/// </remarks>
public sealed class PostgresConnectionStringTests
{
    [Fact]
    public void Keyword_form_is_returned_untouched()
    {
        // Deliberately not round-tripped through a builder. Round-tripping drops any
        // keyword the converter did not think to preserve, which is a large set and a
        // quiet way to lose Pooling or Timeout.
        const string Keyword =
            "Host=localhost;Port=5432;Database=pos;Username=pos_app;Password=x;Pooling=true;Timeout=15";

        Assert.Equal(Keyword, PostgresConnectionString.Normalize(Keyword));
    }

    [Theory]
    [InlineData("postgres://")]
    [InlineData("postgresql://")]
    [InlineData("POSTGRES://")]
    public void Either_scheme_is_recognised(string scheme)
    {
        var normalized = PostgresConnectionString.Normalize(
            $"{scheme}pos_app:secret@db.example.com:5432/pos_production");

        var parsed = new NpgsqlConnectionStringBuilder(normalized);

        Assert.Equal("db.example.com", parsed.Host);
        Assert.Equal("pos_production", parsed.Database);
        Assert.Equal("pos_app", parsed.Username);
        Assert.Equal("secret", parsed.Password);
    }

    [Fact]
    public void A_uri_without_a_port_gets_the_postgres_default()
    {
        var parsed = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize("postgres://u:p@db.example.com/pos"));

        // Uri reports -1 for an absent port, and a literal -1 fails at connect time with a
        // message about the socket rather than about the configuration.
        Assert.Equal(5432, parsed.Port);
    }

    [Fact]
    public void A_password_with_reserved_characters_survives()
    {
        // The case that produces "the credentials are wrong" when it goes wrong, which
        // sends somebody to reset a password that was correct all along. Generated
        // passwords contain these regularly.
        var parsed = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize(
                "postgres://pos_app:p%40ss%2Fword%3A1@db.example.com:5432/pos"));

        Assert.Equal("p@ss/word:1", parsed.Password);
    }

    [Fact]
    public void Query_parameters_are_carried_over()
    {
        // sslmode is the one that matters: several managed hosts require it, it appears
        // nowhere else in the URI, and dropping it silently downgrades the connection.
        var parsed = new NpgsqlConnectionStringBuilder(
            PostgresConnectionString.Normalize(
                "postgres://u:p@db.example.com:5432/pos?sslmode=require"));

        Assert.Equal(SslMode.Require, parsed.SslMode);
    }

    [Fact]
    public void An_unknown_query_parameter_is_refused_rather_than_dropped()
    {
        // Loud, on purpose. A misspelled sslmode that was quietly discarded leaves the
        // connection unencrypted while the configuration looks deliberate.
        Assert.ThrowsAny<Exception>(() => PostgresConnectionString.Normalize(
            "postgres://u:p@db.example.com:5432/pos?sslmodee=require"));
    }

    [Fact]
    public void A_uri_naming_no_database_is_refused()
    {
        var failure = Assert.Throws<ArgumentException>(() =>
            PostgresConnectionString.Normalize("postgres://u:p@db.example.com:5432/"));

        Assert.Contains("names no database", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_value_is_refused(string value) =>
        Assert.ThrowsAny<ArgumentException>(() => PostgresConnectionString.Normalize(value));

    [Fact]
    public void Surrounding_whitespace_is_tolerated() =>
        // What a value pasted out of a dashboard, or read from a file with a trailing
        // newline, actually looks like.
        Assert.Equal(
            "Host=localhost;Database=pos",
            PostgresConnectionString.Normalize("  Host=localhost;Database=pos\n"));
}
