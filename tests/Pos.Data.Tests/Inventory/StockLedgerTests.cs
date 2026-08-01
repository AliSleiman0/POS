using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Inventory;
using Pos.Data.Tests.Catalog;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Inventory;

/// <summary>
/// The ledger is the truth and <c>stock_item.on_hand</c> is a cache of it. Everything here
/// is about that sentence staying true.
/// </summary>
/// <remarks>
/// The invariant — <c>OnHand == SUM(Quantity)</c> for a product — is not something the
/// database can enforce, so it is enforced by the one class allowed to write either of them
/// and asserted here. A drift is silent by nature: both numbers look like perfectly good
/// numbers, and the only way to notice is to add the other one up.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class StockLedgerTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_movement_and_the_cached_total_move_together()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-LEDGER-1", "5099991000011");

        var result = await LedgerOf(scoped).RecordAsync(
            new StockMovementRequest(catalog.ProductId, StockMovementType.Receive, 6m, "Delivery"),
            CancellationToken.None);

        // 12 from the fixture's opening receipt, plus 6.
        Assert.Equal(18.0000m, result.OnHand);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var movement = await reader.Db.StockMovements.FirstAsync(m => m.Id == result.MovementId);

        Assert.Equal(StockMovementType.Receive, movement.Type);
        Assert.Equal(6.0000m, movement.Quantity);
        Assert.Equal("Delivery", movement.Reason);

        var stock = await reader.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId);

        Assert.Equal(18.0000m, stock.OnHand);
    }

    [Fact]
    public async Task The_ledger_stamps_when_and_who_rather_than_taking_them_from_the_caller()
    {
        var actor = Guid.CreateVersion7();
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-LEDGER-WHO", "5099991010011");

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant, actor);

        var before = DateTimeOffset.UtcNow.AddSeconds(-5);

        var result = await LedgerOf(scoped).RecordAsync(
            new StockMovementRequest(catalog.ProductId, StockMovementType.Waste, -1m, "Broken"),
            CancellationToken.None);

        var movement = await scoped.Db.StockMovements.FirstAsync(m => m.Id == result.MovementId);

        // Neither is in StockMovementRequest, and that is the point: a caller who could set
        // them could backdate a write-off or attribute it to somebody else, and the row would
        // not show it.
        Assert.Equal(actor, movement.PerformedBy);
        Assert.InRange(movement.OccurredAt, before, DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task The_first_receipt_of_a_product_creates_its_stock_row()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-LEDGER-2", "5099991020011");

        // A second product, deliberately without the stock row CatalogGraph writes for the
        // first: this is every product a shop has just added, and refusing it would make
        // "receive stock" fail on exactly those.
        var fresh = new Product
        {
            Sku = "SKU-NO-STOCK-ROW",
            Name = "Newly listed",
            TaxClassId = catalog.TaxClassId,
            UnitPrice = 3m,
        };

        scoped.Db.Products.Add(fresh);
        await scoped.Db.SaveChangesAsync();

        Assert.Empty(await scoped.Db.StockItems.Where(s => s.ProductId == fresh.Id).ToListAsync());

        var result = await LedgerOf(scoped).RecordAsync(
            new StockMovementRequest(fresh.Id, StockMovementType.Receive, 4m, "First delivery"),
            CancellationToken.None);

        Assert.Equal(4.0000m, result.OnHand);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        Assert.Equal(4.0000m, (await reader.Db.StockItems.FirstAsync(s => s.ProductId == fresh.Id)).OnHand);
    }

    [Fact]
    public async Task On_hand_equals_the_sum_of_the_ledger_after_a_randomised_sequence()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-LEDGER-3", "5099991030011");

        var ledger = LedgerOf(scoped);

        // Seeded, so a failure is reproducible. The point is not to search for a failing
        // sequence — it is that the invariant does not depend on the order or the mix, which
        // a hand-picked run of three movements cannot demonstrate.
        var random = new Random(20260801);

        for (var i = 0; i < 25; i++)
        {
            var (type, quantity) = random.Next(3) switch
            {
                0 => (StockMovementType.Receive, Math.Round((decimal)random.NextDouble() * 20m, 4)),
                1 => (StockMovementType.Waste, -Math.Round((decimal)random.NextDouble() * 5m, 4)),
                _ => (StockMovementType.Adjust, Math.Round(((decimal)random.NextDouble() - 0.5m) * 10m, 4)),
            };

            if (quantity == 0m)
            {
                continue;
            }

            await ledger.RecordAsync(
                new StockMovementRequest(catalog.ProductId, type, quantity, "Randomised"),
                CancellationToken.None);
        }

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var expected = await reader.Db.StockMovements
            .Where(m => m.ProductId == catalog.ProductId)
            .SumAsync(m => m.Quantity);

        var stored = (await reader.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId)).OnHand;

        Assert.Equal(expected, stored);

        // And the total is not trivially zero, which it would be if nothing had been written.
        Assert.NotEqual(0m, expected);
    }

    [Fact]
    public async Task A_rebuild_reproduces_on_hand_over_a_mixed_history()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-LEDGER-4", "5099991040011");

        var ledger = LedgerOf(scoped);

        await ledger.RecordAsync(
            new StockMovementRequest(catalog.ProductId, StockMovementType.Receive, 10m, "Delivery"),
            CancellationToken.None);

        await ledger.RecordAsync(
            new StockMovementRequest(catalog.ProductId, StockMovementType.Waste, -3m, "Damaged"),
            CancellationToken.None);

        await ledger.RecordAsync(
            new StockMovementRequest(catalog.ProductId, StockMovementType.Adjust, -0.5000m, "Miscount"),
            CancellationToken.None);

        // 12 opening + 10 − 3 − 0.5
        var rebuilt = await ledger.RebuildOnHandAsync(catalog.ProductId, CancellationToken.None);

        Assert.Equal(18.5000m, rebuilt);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        Assert.Equal(
            18.5000m,
            (await reader.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId)).OnHand);
    }

    [Fact]
    public async Task A_rebuild_corrects_a_figure_that_drifted_behind_the_ledgers_back()
    {
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-LEDGER-5", "5099991050011");

        // Drift, written the way it would really happen: a script, an import, or a bug that
        // updated the cache without writing a movement. Without this the rebuild is only
        // being asked to reproduce a number that was already right.
        await using (var saboteur = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            var stock = await saboteur.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId);

            stock.OnHand = 999m;
            await saboteur.Db.SaveChangesAsync();
        }

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var corrected = await LedgerOf(scoped).RebuildOnHandAsync(catalog.ProductId, CancellationToken.None);

        Assert.Equal(12.0000m, corrected);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        Assert.Equal(
            12.0000m,
            (await reader.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId)).OnHand);
    }

    [Fact]
    public async Task A_rebuild_of_everything_reports_how_many_rows_it_corrected()
    {
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-LEDGER-6", "5099991060011");

        await using (var saboteur = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            var stock = await saboteur.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId);

            stock.OnHand = 7m;
            await saboteur.Db.SaveChangesAsync();
        }

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var ledger = LedgerOf(scoped);

        Assert.Equal(1, await ledger.RebuildAllOnHandAsync(CancellationToken.None));

        // Running it twice is the real assertion: a rebuild that reported corrections on a
        // consistent database would be useless as a drift signal, which is the one job the
        // count has.
        Assert.Equal(0, await ledger.RebuildAllOnHandAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_movement_that_cannot_be_written_leaves_neither_the_row_nor_the_total()
    {
        var tenant = Guid.NewGuid();
        var unknownProduct = Guid.CreateVersion7();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        // The foreign key refuses this, part-way through a unit that had already created a
        // stock row and applied the delta to it. If the two writes were not one transaction,
        // the stock row would survive the failure carrying a total no movement explains.
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            LedgerOf(scoped).RecordAsync(
                new StockMovementRequest(unknownProduct, StockMovementType.Receive, 5m, "Nowhere"),
                CancellationToken.None));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        Assert.Empty(await reader.Db.StockItems.Where(s => s.ProductId == unknownProduct).ToListAsync());
        Assert.Empty(await reader.Db.StockMovements.Where(m => m.ProductId == unknownProduct).ToListAsync());
    }

    [Fact]
    public async Task A_movement_that_lost_a_race_is_reported_rather_than_losing_the_other_write()
    {
        var tenant = Guid.NewGuid();

        await using var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(setup.Db, "SKU-LEDGER-7", "5099991070011");

        await using var loser = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        await using var winner = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        // The loser reads the row first, as a till would when it opens the adjustment screen.
        // Its context now holds an xmin the next write invalidates.
        await loser.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId);

        await LedgerOf(winner).RecordAsync(
            new StockMovementRequest(catalog.ProductId, StockMovementType.Receive, 1m, "First"),
            CancellationToken.None);

        // Surfaced as a domain conflict, not a DbUpdateConcurrencyException: the API turns it
        // into a 409 the caller can act on, and the caller is the one who knows whether the
        // stock actually moved.
        await Assert.ThrowsAsync<ConcurrentStockUpdateException>(() =>
            LedgerOf(loser).RecordAsync(
                new StockMovementRequest(catalog.ProductId, StockMovementType.Receive, 1m, "Second"),
                CancellationToken.None));

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        // The winner's write survived, and the invariant still holds — a lost update would
        // leave 14 on hand with only two receipts totalling 13 in the ledger.
        Assert.Equal(
            13.0000m,
            (await reader.Db.StockItems.FirstAsync(s => s.ProductId == catalog.ProductId)).OnHand);

        Assert.Equal(
            13.0000m,
            await reader.Db.StockMovements
                .Where(m => m.ProductId == catalog.ProductId)
                .SumAsync(m => m.Quantity));
    }

    private static IStockLedger LedgerOf(ScopedDbContext scoped) =>
        scoped.Services.GetRequiredService<IStockLedger>();
}
