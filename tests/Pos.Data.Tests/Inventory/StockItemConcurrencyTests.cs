using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Core.Entities;
using Pos.Data.Tests.Catalog;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Inventory;

/// <summary>
/// Stock is the row two registers fight over, and <c>xmin</c> is what makes them collide
/// loudly instead of one silently overwriting the other's count.
/// </summary>
/// <remarks>
/// The mechanism Phase 3.6 is built on, tested here where it is introduced rather than
/// there. A last-unit oversell caused by a lost update is not detectable after the fact:
/// both writes succeeded, both look correct, and the only evidence is a stock count that
/// disagrees with the ledger.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class StockItemConcurrencyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task The_row_version_is_the_xmin_system_column()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        var property = scoped.Db.Model
            .FindEntityType(typeof(StockItem))!
            .FindProperty(nameof(StockItem.RowVersion))!;

        Assert.Equal("xmin", property.GetColumnName());
        Assert.Equal("xid", property.GetColumnType());
        Assert.True(property.IsConcurrencyToken);

        // The half that matters. Without it this test passes on a broken mapping: EF would
        // maintain a real row_version column, concurrency would appear to work, and the
        // token would no longer be tied to the row's transaction id — so an update from raw
        // SQL, a trigger or a restore would stop invalidating it.
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'stock_item' AND column_name = 'row_version';
            """;

        Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task A_concurrent_update_to_the_same_stock_item_is_rejected()
    {
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-CONTENDED", "5099999010011");

        // Two contexts, as two registers would be: each reads the row, then each writes.
        await using var first = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        await using var second = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var firstView = await first.Db.StockItems.FirstAsync(s => s.Id == catalog.StockItemId);
        var secondView = await second.Db.StockItems.FirstAsync(s => s.Id == catalog.StockItemId);

        firstView.OnHand -= 1m;
        await first.Db.SaveChangesAsync();

        secondView.OnHand -= 1m;

        // Without the token this succeeds and the shop has sold two units while
        // decrementing one. The exception is the point: it hands the decision back to code
        // that can re-read and retry, rather than losing the first write.
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task An_uncontended_update_succeeds()
    {
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-QUIET", "5099999020011");

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var stock = await scoped.Db.StockItems.FirstAsync(s => s.Id == catalog.StockItemId);

        stock.OnHand -= 1m;
        await scoped.Db.SaveChangesAsync();

        // The control. Without it, the test above could be green because every update
        // fails — a concurrency token misconfigured to never match looks identical from
        // the outside.
        Assert.Equal(11.0000m, stock.OnHand);
    }

    [Fact]
    public async Task Two_successive_updates_through_one_context_succeed()
    {
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-TWICE", "5099999030011");

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var stock = await scoped.Db.StockItems.FirstAsync(s => s.Id == catalog.StockItemId);

        stock.OnHand -= 1m;
        await scoped.Db.SaveChangesAsync();

        stock.OnHand -= 1m;

        // Catches a token marked as a concurrency token but not generated on update: the
        // first save works, and the second compares against a value the database has since
        // changed and throws with nothing concurrent happening at all. EF has to read the
        // new xmin back after each write for a till to sell twice in a row.
        await scoped.Db.SaveChangesAsync();

        Assert.Equal(10.0000m, stock.OnHand);
    }
}
