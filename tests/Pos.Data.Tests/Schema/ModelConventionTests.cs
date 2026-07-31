using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Schema;

/// <summary>
/// The model-wide conventions, asserted on an entity that configures none of them.
/// </summary>
/// <remarks>
/// Testing conventions on a configured entity tests the configuration. These assertions
/// use <see cref="ThrowawayEntity"/>, which does nothing but derive from
/// <c>TenantEntity</c> — the same thing every Phase 2 entity will do.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class ModelConventionTests(PostgresFixture postgres)
{
    [Fact]
    public void Decimal_properties_map_to_numeric_19_4_without_being_configured()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        var columnType = context.Model
            .FindEntityType(typeof(ThrowawayEntity))!
            .FindProperty(nameof(ThrowawayEntity.Amount))!
            .GetColumnType();

        // The provider default here is numeric(18,2), which would silently drop the 3rd and
        // 4th decimals of a unit price like 0.1650 — invisible until a total is a cent out.
        Assert.Equal("numeric(19,4)", columnType);
    }

    [Fact]
    public void Instants_map_to_timestamptz_without_being_configured()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        var columnType = context.Model
            .FindEntityType(typeof(ThrowawayEntity))!
            .FindProperty(nameof(TenantEntity.CreatedAt))!
            .GetColumnType();

        Assert.Equal("timestamptz", columnType);
    }

    [Fact]
    public void Names_are_snake_case_without_being_configured()
    {
        using var context = ThrowawayEntityDbContext.Create(postgres.ConnectionString, new SystemTenantContext());

        var entityType = context.Model.FindEntityType(typeof(ThrowawayEntity))!;

        Assert.Equal("throwaway_entities", entityType.GetTableName());
        Assert.Equal("tenant_id", entityType.FindProperty(nameof(ITenantOwned.TenantId))!.GetColumnName());
    }

    [Fact]
    public async Task The_tenant_table_carries_its_check_constraints()
    {
        // Constraints in the model are not constraints in the database until a migration
        // says so, and a migration nobody read is how a destructive change ships.
        var names = await QueryStringsAsync(
            """
            SELECT conname FROM pg_constraint
            WHERE conrelid = 'tenant'::regclass AND contype = 'c'
            ORDER BY conname;
            """);

        Assert.Contains("ck_tenant_slug_format", names, StringComparer.Ordinal);
        Assert.Contains("ck_tenant_tax_mode_allowed", names, StringComparer.Ordinal);
        Assert.Contains("ck_tenant_name_length", names, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_tenant_slug_constraint_actually_rejects_a_bad_slug()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        scoped.Db.Tenants.Add(new Tenant
        {
            Id = Guid.CreateVersion7(),
            Name = "Bad Slug",
            Slug = "Not A Slug",
            CurrencyCode = "EUR",
            TimeZoneId = "Europe/Dublin",
        });

        // Enforced in the database, not only in NormalizeSlug: a row inserted by a script
        // or a future admin tool has to obey the same rule the login path assumes.
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => scoped.Db.SaveChangesAsync());
        Assert.IsType<PostgresException>(ex.InnerException);
    }

    private async Task<List<string>> QueryStringsAsync(string sql)
    {
        var results = new List<string>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
