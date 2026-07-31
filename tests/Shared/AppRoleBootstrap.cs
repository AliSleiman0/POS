using Npgsql;

namespace Pos.TestSupport;

/// <summary>
/// Creates the <c>pos_app</c> role inside a test container and derives its connection
/// string.
/// </summary>
/// <remarks>
/// In dev and production this role comes from <c>docker/postgres-init/01-app-role.sh</c>,
/// which only runs against an empty data directory. A Testcontainer is created by the
/// Postgres image's own entrypoint with no such script mounted, so the tests do the same
/// thing themselves — the alternative is running the whole suite as the superuser, which
/// bypasses row-level security unconditionally and would make every RLS assertion pass
/// for the wrong reason.
/// <para>
/// Linked into both <c>Pos.Data.Tests</c> and <c>Pos.Api.Tests</c> from <c>tests/Shared</c>.
/// One copy, because two copies of a security fixture drift and only one of them is read.
/// </para>
/// </remarks>
public static class AppRoleBootstrap
{
    public const string RoleName = "pos_app";

    private const string Password = "test_only_not_a_secret";

    /// <summary>Creates the role. Call before migrating.</summary>
    public static async Task CreateAsync(string ownerConnectionString)
    {
        await using var connection = new NpgsqlConnection(ownerConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // NOBYPASSRLS stated rather than assumed. It is the default, but this role exists
        // for exactly one reason and it should be readable in the place it is created.
        command.CommandText = $"""
            DO $$
            BEGIN
              IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '{RoleName}') THEN
                CREATE ROLE {RoleName} WITH LOGIN NOBYPASSRLS PASSWORD '{Password}';
              END IF;
            END $$;
            """;

        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// The same database, connected as <c>pos_app</c> — which is how the application
    /// connects, and therefore how the tests must.
    /// </summary>
    public static string ConnectionStringFor(string ownerConnectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(ownerConnectionString)
        {
            Username = RoleName,
            Password = Password,
        };

        // Both defaults, restated because the RLS design depends on them and a future
        // "performance tweak" to either would break isolation silently. app.tenant_id is a
        // session variable; multiplexing interleaves sessions, and No Reset On Close keeps
        // the previous request's value alive on a pooled connection.
        builder.Multiplexing = false;
        builder.NoResetOnClose = false;

        return builder.ConnectionString;
    }
}
