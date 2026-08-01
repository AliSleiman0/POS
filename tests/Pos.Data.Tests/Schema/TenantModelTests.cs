using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Schema;

/// <summary>
/// Rules that must hold for <b>every</b> tenant-owned entity, asserted by walking the model
/// rather than by naming the entities.
/// </summary>
/// <remarks>
/// Written this way on purpose. A list of entity names is correct on the day it is written
/// and silently incomplete when Phase 3 adds <c>Sale</c>, <c>SaleLine</c> and <c>Tender</c>
/// — and the failure mode of a missed entity is an unfiltered read, which looks exactly
/// like working software. These inherit the same shape as
/// <c>RowLevelSecurityTests.Every_tenant_owned_table_is_covered</c>: enumerate, then report
/// the offenders by name.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class TenantModelTests(PostgresFixture postgres)
{
    /// <summary>
    /// The entities whose mapping is ours to get right.
    /// </summary>
    /// <remarks>
    /// Scoping to this namespace is required, not tidiness: Identity brings its own indexes
    /// (<c>ix_user_claim_user_id</c>, <c>RoleNameIndex</c>, the composite keys on the join
    /// tables) which do not lead with <c>tenant_id</c> and are not ours to change. The rule
    /// being asserted is "every index we declare on a domain entity", and this is what
    /// makes that statement checkable.
    /// </remarks>
    private const string DomainNamespace = "Pos.Core.Entities";

    [Fact]
    public async Task Every_tenant_owned_entity_has_a_query_filter()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var tenantOwned = TenantOwnedEntities(scoped.Db).ToArray();

        var unfiltered = tenantOwned
            .Where(e => e.GetDeclaredQueryFilters().Count == 0)
            .Select(e => e.DisplayName())
            .ToArray();

        Assert.True(
            unfiltered.Length == 0,
            "Tenant-owned entities with no query filter: " + string.Join(", ", unfiltered));

        // Guards against the assertion above passing because nothing was enumerated — the
        // way this test would fail to notice a filter loop that stopped running at all.
        Assert.Contains(tenantOwned, e => e.ClrType == typeof(Product));
        Assert.True(tenantOwned.Length >= 7, $"Only {tenantOwned.Length} tenant-owned entities were found.");
    }

    [Fact]
    public async Task Every_index_on_a_domain_entity_leads_with_the_tenant()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var offenders = DomainEntities(scoped.Db)
            .SelectMany(e => e.GetIndexes()
                .Where(i => i.Properties[0].Name != nameof(ITenantOwned.TenantId))
                .Select(i => $"{e.DisplayName()}.{i.GetDatabaseName()}"))
            .ToArray();

        // Every query carries `tenant_id = @p`, added by the filter. An index that does not
        // lead with it cannot be used to satisfy that predicate, so the query falls back to
        // a scan — on the barcode table, that is a scan on every scan at every till.
        Assert.True(
            offenders.Length == 0,
            "Indexes not leading with TenantId: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task Every_key_other_than_the_primary_key_leads_with_the_tenant()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var offenders = DomainEntities(scoped.Db)
            .SelectMany(e => e.GetKeys()
                .Where(k => !k.IsPrimaryKey())
                .Where(k => k.Properties[0].Name != nameof(ITenantOwned.TenantId))
                .Select(k => $"{e.DisplayName()}.{k.GetName()}"))
            .ToArray();

        // The alternate keys exist to be the target of a tenant-scoped foreign key. One
        // that did not lead with the tenant would still be a valid key and would quietly
        // stop being the thing that makes a cross-tenant reference impossible.
        Assert.True(
            offenders.Length == 0,
            "Alternate keys not leading with TenantId: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task Every_tenant_scoped_relationship_carries_the_tenant_in_its_foreign_key()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var offenders = DomainEntities(scoped.Db)
            .SelectMany(e => e.GetForeignKeys()
                .Where(fk => fk.Properties[0].Name != nameof(ITenantOwned.TenantId))
                .Select(fk => $"{e.DisplayName()} -> {fk.PrincipalEntityType.DisplayName()}"))
            .ToArray();

        // Postgres exempts referential integrity checks from row-level security, so a
        // single-column foreign key would accept a reference to another tenant's row. The
        // tenant has to be inside the key, not merely checked alongside it.
        Assert.True(
            offenders.Length == 0,
            "Foreign keys not carrying the tenant: " + string.Join(", ", offenders));
    }

    [Fact]
    public async Task The_tax_rate_is_narrower_than_the_model_wide_decimal_convention()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var columnType = scoped.Db.Model
            .FindEntityType(typeof(TaxClass))!
            .FindProperty(nameof(TaxClass.Rate))!
            .GetColumnType();

        // The convention makes every decimal numeric(19,4), which stores a rate perfectly
        // well — so an override that silently failed to apply would never be noticed by a
        // value that round-trips. It is the width that matters: (19,4) also stores 2000,
        // which is what "20%" typed as 20 becomes.
        Assert.Equal("numeric(6,4)", columnType);
    }

    private static IEnumerable<IEntityType> TenantOwnedEntities(AppDbContext db) =>
        db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && e.BaseType is null);

    private static IEnumerable<IEntityType> DomainEntities(AppDbContext db) =>
        TenantOwnedEntities(db).Where(e => e.ClrType.Namespace == DomainNamespace);
}
