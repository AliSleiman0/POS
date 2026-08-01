using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pos.Core.Entities;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Catalog;

/// <summary>
/// The catalog's foreign keys carry <c>tenant_id</c>, and this is what proves it buys
/// something.
/// </summary>
/// <remarks>
/// <b>Every test here runs as <c>pos_app</c>, not the owner.</b> The owner is a superuser
/// and bypasses row-level security, so a result obtained there says nothing about the
/// deployed configuration. Running as the application role also pins the Postgres
/// behaviour the whole design rests on: <b>referential integrity checks are exempt from row
/// security</b>. A single-column key on <c>product_id</c> would therefore accept a barcode
/// pointing at another tenant's product — the check does not see the policy that hides the
/// row. That is the failure these tests exist to make impossible.
/// <para>
/// The negatives are paired with positives on the same connection. Without them, a green
/// "the insert was rejected" is indistinguishable from an insert that failed for some
/// unrelated reason under RLS.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class CatalogForeignKeyTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_barcode_for_a_product_in_the_same_tenant_is_accepted()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db);

        scoped.Db.Barcodes.Add(new Barcode { ProductId = catalog.ProductId, Code = "5099999000028" });
        await scoped.Db.SaveChangesAsync();

        // The positive control. Everything below asserts that a write is refused, and none
        // of those results mean anything unless this one succeeds.
        Assert.Equal(2, await scoped.Db.Barcodes.CountAsync(b => b.ProductId == catalog.ProductId));
    }

    [Fact]
    public async Task A_barcode_cannot_reference_another_tenants_product()
    {
        var victim = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        await using var victimScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, victim);
        var catalog = await CatalogGraph.WriteAsync(victimScope.Db, "SKU-VICTIM", "5099999100011");

        await using var attackerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, attacker);

        // The interceptor stamps this row with the attacker's tenant, so the foreign key
        // looks for (attacker, victim's product) — a pair that does not exist.
        attackerScope.Db.Barcodes.Add(new Barcode { ProductId = catalog.ProductId, Code = "5099999100028" });

        await AssertViolatesAsync(attackerScope, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_stock_item_cannot_reference_another_tenants_product()
    {
        var victim = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        await using var victimScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, victim);
        var catalog = await CatalogGraph.WriteAsync(victimScope.Db, "SKU-VICTIM-2", "5099999200011");

        await using var attackerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, attacker);
        attackerScope.Db.StockItems.Add(new StockItem { ProductId = catalog.ProductId, OnHand = 999m });

        // Worth its own test rather than trusting the barcode one: stock is the row an
        // attacker would actually want to write into someone else's shop.
        await AssertViolatesAsync(attackerScope, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_stock_movement_for_a_product_in_the_same_tenant_is_accepted()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-LEDGER", "5099999010011");

        scoped.Db.StockMovements.Add(new StockMovement
        {
            ProductId = catalog.ProductId,
            Type = StockMovementType.Waste,
            Quantity = -2.0000m,
            Reason = "Damaged in transit",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        await scoped.Db.SaveChangesAsync();

        // The positive control for the negative below, and also the only place a negative
        // quantity is written straight to the column — the ledger is signed and the check
        // constraints deliberately do not police the sign.
        Assert.Equal(2, await scoped.Db.StockMovements.CountAsync(m => m.ProductId == catalog.ProductId));
    }

    [Fact]
    public async Task A_stock_movement_cannot_reference_another_tenants_product()
    {
        var victim = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        await using var victimScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, victim);
        var catalog = await CatalogGraph.WriteAsync(victimScope.Db, "SKU-VICTIM-4", "5099999020011");

        await using var attackerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, attacker);

        attackerScope.Db.StockMovements.Add(new StockMovement
        {
            ProductId = catalog.ProductId,
            Type = StockMovementType.Receive,
            Quantity = 500m,
            Reason = "Not my product",
            OccurredAt = DateTimeOffset.UtcNow,
        });

        // Worth its own test for the same reason the stock-item one is: the ledger is what a
        // report reads, so a movement written into someone else's shop would show up as
        // stock they never received and cannot explain.
        await AssertViolatesAsync(attackerScope, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_movement_type_the_enum_does_not_have_is_rejected()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-BADTYPE", "5099999030011");

        // Raw SQL, because C# cannot produce this value — which is the point.
        // ck_stock_movement_type_allowed is what stops an import or a hand-written script
        // writing a type the application has no case for, and the enum's exhaustiveness is
        // only a compile-time guarantee.
        var sql = """
            INSERT INTO stock_movement
                (id, tenant_id, product_id, type, quantity, occurred_at, created_at)
            VALUES ({0}, {1}, {2}, 'Shrinkage', -1, now(), now())
            """;

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            scoped.Db.Database.ExecuteSqlRawAsync(
                sql.Replace("{0}", $"'{Guid.CreateVersion7()}'", StringComparison.Ordinal)
                   .Replace("{1}", $"'{tenant}'", StringComparison.Ordinal)
                   .Replace("{2}", $"'{catalog.ProductId}'", StringComparison.Ordinal)));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    [Fact]
    public async Task A_product_cannot_reference_another_tenants_tax_class()
    {
        var victim = Guid.NewGuid();
        var attacker = Guid.NewGuid();

        await using var victimScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, victim);
        var catalog = await CatalogGraph.WriteAsync(victimScope.Db, "SKU-VICTIM-3", "5099999300011");

        await using var attackerScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, attacker);

        attackerScope.Db.Products.Add(new Product
        {
            Sku = "SKU-BORROWED",
            Name = "Borrowed Tax Class",
            TaxClassId = catalog.TaxClassId,
            UnitPrice = 1m,
        });

        // Borrowing another tenant's tax class would price this shop's goods from a rate
        // it cannot see and cannot change.
        await AssertViolatesAsync(attackerScope, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_product_that_still_has_barcodes_cannot_be_deleted()
    {
        var tenant = Guid.NewGuid();
        Guid productId;

        await using (var writer = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            productId = (await CatalogGraph.WriteAsync(writer.Db, "SKU-RESTRICT", "5099999400011")).ProductId;
        }

        // A second scope, so the barcode is not in this context's change tracker. That
        // detail is the test: with the dependent loaded, EF refuses client-side with
        // "the association has been severed" and never issues a DELETE — which is a fine
        // second line of defence but proves nothing about the schema. What has to hold is
        // that the *database* refuses, because that is what still holds for a delete issued
        // by a script, a report tool or a future bulk operation.
        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var product = await scoped.Db.Products.FirstAsync(p => p.Id == productId);
        scoped.Db.Products.Remove(product);

        // RESTRICT, not CASCADE. Products are withdrawn with IsActive and never deleted —
        // a cascade would quietly take the barcodes, and later the sale lines, with it.
        await AssertViolatesAsync(scoped, PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task A_duplicate_barcode_code_within_a_tenant_is_rejected()
    {
        var tenant = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var catalog = await CatalogGraph.WriteAsync(scoped.Db, "SKU-DUPE", "5099999500011");

        scoped.Db.Barcodes.Add(new Barcode { ProductId = catalog.ProductId, Code = "5099999500011" });

        // One code cannot scan to two products, or the register has to guess.
        await AssertViolatesAsync(scoped, PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task The_same_barcode_code_can_exist_in_two_tenants()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        const string SharedCode = "5099999600011";

        await using var firstScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, first);
        await CatalogGraph.WriteAsync(firstScope.Db, "SKU-SHARED", SharedCode);

        await using var secondScope = ScopedDbContext.ForTenant(postgres.AppConnectionString, second);
        await CatalogGraph.WriteAsync(secondScope.Db, "SKU-SHARED", SharedCode);

        // Two shops stocking the same tin of beans is the normal case, not a conflict.
        // Uniqueness that was not per-tenant would make the second shop unable to add it.
        Assert.Equal(1, await secondScope.Db.Barcodes.CountAsync(b => b.Code == SharedCode));
    }

    [Fact]
    public async Task A_product_that_does_not_track_stock_is_stored_as_not_tracking_stock()
    {
        var tenant = Guid.NewGuid();
        Guid productId;

        await using (var writer = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            var catalog = await CatalogGraph.WriteAsync(writer.Db, "SKU-SERVICE", "5099999700011");

            var service = new Product
            {
                Sku = "SKU-SERVICE-2",
                Name = "Coffee to Go",
                TaxClassId = catalog.TaxClassId,
                UnitPrice = 3.5000m,
                TrackStock = false,
            };

            writer.Db.Products.Add(service);
            await writer.Db.SaveChangesAsync();
            productId = service.Id;
        }

        // Read back through a fresh context, so the assertion is about the row and not
        // about the object still sitting in the first context's identity map.
        //
        // This is the quietest failure in the milestone. track_stock has a store default of
        // true, and EF decides whether to send a property on INSERT by comparing it to the
        // sentinel — the value meaning "not set". If that were left to inference and got it
        // wrong, `false` would be dropped from the INSERT and the database would write
        // `true`: a service item that starts accumulating stock nobody asked it to track.
        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);
        var stored = await reader.Db.Products.FirstAsync(p => p.Id == productId);

        Assert.False(stored.TrackStock);
    }

    private static async Task AssertViolatesAsync(ScopedDbContext scoped, string sqlState)
    {
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => scoped.Db.SaveChangesAsync());
        var postgresException = Assert.IsType<PostgresException>(ex.InnerException);

        // Assert on the SQL state, not the message: the message is localised and carries a
        // constraint name that a rename would change, and "some exception was thrown" is
        // exactly the assertion that keeps passing after the constraint is gone.
        Assert.Equal(sqlState, postgresException.SqlState);
    }
}
