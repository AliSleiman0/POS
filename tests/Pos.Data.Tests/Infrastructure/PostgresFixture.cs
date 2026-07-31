using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pos.TestSupport;
using Testcontainers.PostgreSql;

namespace Pos.Data.Tests.Infrastructure;

/// <summary>
/// One Postgres container for the whole assembly, migrated once.
/// </summary>
/// <remarks>
/// A container per test would be correct and unusably slow. Tests isolate themselves by
/// using a fresh tenant id instead — which is not a workaround but the thing under test:
/// if two tenants in one database can see each other, the suite should fail.
/// </remarks>
public sealed class PostgresFixture : IAsyncLifetime
{
    // Pinned to the same major version as docker-compose.yml. Row-level security
    // behaviour is version-sensitive, so "whatever latest is" is not good enough.
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("pos_test")
        .WithUsername("pos")
        .WithPassword("test_only_not_a_secret")
        .Build();

    /// <summary>
    /// Connected as the schema owner — a superuser, which <b>bypasses row-level security
    /// unconditionally</b>. Correct for migrations and for a test that needs to see across
    /// tenants to prove something was written. Never how the application connects.
    /// </summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>
    /// Connected as <c>pos_app</c>, which is how the application connects and therefore the
    /// only connection an isolation assertion means anything on.
    /// </summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await _container.StartAsync();

        // Before migrating: the RLS migration refuses to run without this role, deliberately,
        // rather than granting to a role that does not exist and failing halfway.
        await AppRoleBootstrap.CreateAsync(ConnectionString);
        AppConnectionString = AppRoleBootstrap.ConnectionStringFor(ConnectionString);

        // Migrations, not EnsureCreated: the schema under test has to be the schema the
        // migrations produce, or the suite validates a model that never reaches a database.
        var services = new ServiceCollection();
        services.AddPosData(ConnectionString);

        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _container.DisposeAsync();
    }

    /// <summary>Empties every table, for the few tests that assert on global row counts.</summary>
    public async Task ResetAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            DO $$
            DECLARE statement text;
            BEGIN
              SELECT 'TRUNCATE TABLE ' || string_agg(format('%I.%I', schemaname, tablename), ', ') || ' CASCADE'
              INTO statement
              FROM pg_tables
              WHERE schemaname = 'public' AND tablename <> '__EFMigrationsHistory';

              IF statement IS NOT NULL THEN EXECUTE statement; END IF;
            END $$;
            """;

        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Shares one container across every test class that opts in with
/// <c>[Collection(PostgresCollection.Name)]</c>.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres";
}
