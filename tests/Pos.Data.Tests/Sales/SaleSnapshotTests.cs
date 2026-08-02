using Microsoft.EntityFrameworkCore;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data.Tests.Catalog;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Sales;

/// <summary>
/// CLAUDE.md invariant 5: a sale line holds its own copy of everything that could later change.
/// </summary>
/// <remarks>
/// The failure this prevents is silent and unrecoverable. Raise a price on Tuesday and a
/// report that joins to <c>Product.UnitPrice</c> rewrites Monday's revenue — the reports stop
/// reconciling with the cash that was actually taken, both figures look reasonable, and
/// nothing surfaces the discrepancy. By the time anyone notices, the correct numbers no longer
/// exist anywhere.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class SaleSnapshotTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_sale_total_is_unchanged_after_the_products_price_and_tax_rate_change()
    {
        var tenant = await CreateTenantAsync();
        Guid saleId;
        SeededCatalog catalog;

        await using (var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            catalog = await CatalogGraph.WriteAsync(scoped.Db);
            saleId = await WriteSaleAsync(scoped.Db, catalog, unitPrice: 1.2000m, taxRate: 0.2300m);

            // The catalog moves on: a price rise and a legislated rate change, both entirely
            // ordinary, and both after the sale was completed.
            var product = await scoped.Db.Products.FirstAsync(p => p.Id == catalog.ProductId);
            product.UnitPrice = (Money)9.9900m;

            var taxClass = await scoped.Db.TaxClasses.FirstAsync(t => t.Id == catalog.TaxClassId);
            taxClass.Rate = 0.0900m;

            await scoped.Db.SaveChangesAsync();
        }

        // A second scope, so the answer comes from the database rather than from a change
        // tracker still holding the instances that were written.
        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var sale = await reader.Db.Sales.AsNoTracking().FirstAsync(s => s.Id == saleId);
        var line = await reader.Db.SaleLines.AsNoTracking().FirstAsync(l => l.SaleId == saleId);

        Assert.Equal((Money)1.2000m, line.UnitPrice);
        Assert.Equal(0.2300m, line.TaxRate);
        Assert.Equal((Money)2.4000m, line.LineSubtotal);
        Assert.Equal((Money)2.95m, sale.Total);

        // And the current catalog really did move, so the assertions above are not passing
        // because nothing changed.
        var current = await reader.Db.Products.AsNoTracking().FirstAsync(p => p.Id == catalog.ProductId);
        Assert.Equal((Money)9.9900m, current.UnitPrice);
    }

    [Fact]
    public async Task A_sale_line_keeps_the_description_it_was_sold_under()
    {
        var tenant = await CreateTenantAsync();
        Guid saleId;

        await using (var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            var catalog = await CatalogGraph.WriteAsync(scoped.Db);
            saleId = await WriteSaleAsync(scoped.Db, catalog, unitPrice: 1.2000m, taxRate: 0.2300m);

            var product = await scoped.Db.Products.FirstAsync(p => p.Id == catalog.ProductId);
            product.Name = "Sparkling Water 500ml";

            await scoped.Db.SaveChangesAsync();
        }

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var line = await reader.Db.SaleLines.AsNoTracking().FirstAsync(l => l.SaleId == saleId);

        // A receipt reprinted a year later has to say what the customer bought, not what the
        // product is called now. Rebranding an item would otherwise rewrite every past receipt.
        Assert.Equal("Still Water 500ml", line.Description);
    }

    [Fact]
    public async Task A_report_over_the_period_is_unchanged_too()
    {
        // The snapshot is only worth having if the READ path honours it. A sale line that
        // stores its own price, read by a query that joins to the product anyway, protects
        // nothing — so this asserts on an aggregate built the way a report would build one.
        var tenant = await CreateTenantAsync();

        await using (var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant))
        {
            var catalog = await CatalogGraph.WriteAsync(scoped.Db);
            await WriteSaleAsync(scoped.Db, catalog, unitPrice: 1.2000m, taxRate: 0.2300m);

            var product = await scoped.Db.Products.FirstAsync(p => p.Id == catalog.ProductId);
            product.UnitPrice = (Money)9.9900m;

            await scoped.Db.SaveChangesAsync();
        }

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenant);

        var lines = await reader.Db.SaleLines.AsNoTracking().ToListAsync();

        var revenue = Money.Sum(lines.Select(l => l.LineTotal));

        Assert.Equal((Money)2.9520m, revenue);

        // The shape of the wrong query, stated so the difference is visible rather than
        // asserted in prose: joining to the catalog gives 19.98 for the same two units, and
        // it is the more obvious query to write.
        var wrong = await reader.Db.SaleLines
            .AsNoTracking()
            .Join(
                reader.Db.Products,
                line => line.ProductId,
                product => product.Id,
                (line, product) => product.UnitPrice * line.Quantity)
            .ToListAsync();

        Assert.Equal((Money)19.9800m, Money.Sum(wrong));
    }

    /// <summary>
    /// Writes one completed two-unit sale, in the principals-then-dependents order the rest of
    /// this suite uses — there are still no navigation properties, so EF fixes up nothing.
    /// </summary>
    private static async Task<Guid> WriteSaleAsync(
        AppDbContext db,
        SeededCatalog catalog,
        decimal unitPrice,
        decimal taxRate)
    {
        var register = new Register { Name = "Front Counter", IsActive = true };
        db.Registers.Add(register);
        await db.SaveChangesAsync();

        var shift = new Shift
        {
            RegisterId = register.Id,
            OpenedBy = Guid.CreateVersion7(),
            OpenedAt = DateTimeOffset.UtcNow,
            OpeningFloat = (Money)100m,
        };

        db.Shifts.Add(shift);
        await db.SaveChangesAsync();

        // 2 × 1.2000 = 2.4000 net, 23% tax = 0.5520, total 2.9520 -> the header rounds to 2.95.
        var sale = new Sale
        {
            SaleNumber = 1,
            ClientTransactionId = Guid.CreateVersion7(),
            RegisterId = register.Id,
            ShiftId = shift.Id,
            CashierId = Guid.CreateVersion7(),
            Type = SaleType.Sale,
            Status = SaleStatus.Completed,
            TaxMode = TaxMode.Exclusive,
            Subtotal = (Money)2.4000m,
            DiscountTotal = Money.Zero,
            TaxTotal = (Money)0.55m,
            RoundingAdjustment = Money.Zero,
            Total = (Money)2.95m,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        db.SaleLines.Add(new SaleLine
        {
            SaleId = sale.Id,
            ProductId = catalog.ProductId,
            LineNumber = 1,
            Description = "Still Water 500ml",
            Quantity = 2m,
            UnitPrice = (Money)unitPrice,
            TaxRate = taxRate,
            DiscountAmount = Money.Zero,
            LineSubtotal = (Money)(unitPrice * 2m),
            LineTax = (Money)(unitPrice * 2m * taxRate),
            LineTotal = (Money)(unitPrice * 2m * (1m + taxRate)),
        });

        db.Tenders.Add(new Tender
        {
            SaleId = sale.Id,
            Method = TenderMethod.Cash,
            Amount = (Money)5m,
            ChangeGiven = (Money)2.05m,
        });

        await db.SaveChangesAsync();

        return sale.Id;
    }

    private async Task<Guid> CreateTenantAsync()
    {
        var tenantId = Guid.CreateVersion7();

        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        scoped.Db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Name = "Snapshot Tenant",
            Slug = $"t-{tenantId:N}"[..20],
            CurrencyCode = "EUR",
            TimeZoneId = "Europe/Dublin",
        });

        await scoped.Db.SaveChangesAsync();

        return tenantId;
    }
}
