using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;
using Pos.Core.Sales;
using Pos.Data.Tests.Catalog;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Sales;

/// <summary>
/// The ledger invariant, held across a randomised sequence of sales, voids and refunds.
/// </summary>
/// <remarks>
/// <c>StockItem.OnHand == SUM(StockMovement.Quantity)</c> for every product, always. Phase 2.4
/// proved it for hand-written adjustments; this proves it survives the paths Phase 3 added,
/// which move stock through a transaction the ledger does not own.
/// <para>
/// Seeded, so a failure is reproducible rather than a flake somebody reruns. The sequence is
/// random because the interesting failures are ordering-dependent — a void after a partial
/// refund, two sales of the same product in one basket — and those are exactly the orders
/// nobody writes by hand.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class SaleLedgerPropertyTests(PostgresFixture postgres)
{
    private const int Seed = 20260802;

    private const int Operations = 40;

    [Fact]
    public async Task On_hand_always_equals_the_movement_sum_after_a_randomised_sale_sequence()
    {
        var world = await ArrangeAsync();
        var random = new Random(Seed);

        var completed = new List<Guid>();

        for (var step = 0; step < Operations; step++)
        {
            await using var scoped = ScopedDbContext.ForTenant(
                postgres.AppConnectionString, world.TenantId, world.CashierId);

            var writer = scoped.Services.GetRequiredService<ISaleWriter>();

            try
            {
                // Weighted towards selling, so there is always something to void or refund.
                var action = random.Next(0, 10);

                if (action < 6 || completed.Count == 0)
                {
                    completed.Add(await SellAsync(writer, world, random));
                }
                else if (action < 8)
                {
                    var target = completed[random.Next(completed.Count)];
                    await writer.VoidAsync(target, "Randomised void", null, CancellationToken.None);
                }
                else
                {
                    var target = completed[random.Next(completed.Count)];
                    await RefundAsync(writer, scoped, world, target, random);
                }
            }
            catch (Core.Exceptions.PosDomainException)
            {
                // A refused operation is a legitimate outcome, not a failed property: the
                // generator picks targets at random, so it will ask to void something already
                // voided or refund more than remains. What matters is that a refusal leaves
                // the invariant intact, which the assertion below checks either way.
            }
        }

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        var stock = await reader.Db.StockItems.AsNoTracking().ToListAsync();

        Assert.NotEmpty(stock);

        foreach (var item in stock)
        {
            var ledger = await reader.Db.StockMovements
                .Where(m => m.ProductId == item.ProductId)
                .SumAsync(m => m.Quantity);

            // CatalogGraph writes the opening receipt as a movement, unlike the API fixture,
            // so the two really are equal here with nothing added back.
            Assert.Equal(ledger, item.OnHand);
        }

        // And the sequence actually did something — a property that held because nothing ever
        // committed would be worthless.
        Assert.NotEmpty(await reader.Db.Sales.ToListAsync());
        Assert.NotEmpty(await reader.Db.StockMovements.Where(m => m.SaleId != null).ToListAsync());
    }

    private static async Task<Guid> SellAsync(ISaleWriter writer, SaleWorld world, Random random)
    {
        var quantity = random.Next(1, 4);

        var cart = new Cart(
            [
                new CartLine(
                    world.ProductId,
                    "Still Water 500ml",
                    quantity,
                    (Money)1.2000m,
                    0.2300m,
                    Money.Zero),
            ],
            Money.Zero,
            TaxMode.Exclusive);

        var priced = PricingEngine.Price(cart);

        var result = await writer.CommitAsync(
            new SaleCommitRequest(
                Guid.CreateVersion7(),
                world.RegisterId,
                world.ShiftId,
                TaxMode.Exclusive,
                priced,
                [new TenderInstruction(TenderMethod.Cash, (Money)100m)],
                new HashSet<Guid> { world.ProductId }),
            null,
            CancellationToken.None);

        return result.SaleId;
    }

    private static async Task RefundAsync(
        ISaleWriter writer,
        ScopedDbContext scoped,
        SaleWorld world,
        Guid saleId,
        Random random)
    {
        var line = await scoped.Db.SaleLines
            .AsNoTracking()
            .FirstOrDefaultAsync(l => l.SaleId == saleId);

        if (line is null)
        {
            return;
        }

        var quantity = Math.Min(line.Quantity, random.Next(1, 3));

        await writer.RefundAsync(
            new SaleRefundRequest(
                saleId,
                Guid.CreateVersion7(),
                world.RegisterId,
                world.ShiftId,
                "Randomised refund",
                [new RefundLineInstruction(line.Id, quantity)],
                CashRoundingIncrement: 0m),
            null,
            CancellationToken.None);
    }

    private sealed record SaleWorld(Guid TenantId, Guid CashierId, Guid RegisterId, Guid ShiftId, Guid ProductId);

    private async Task<SaleWorld> ArrangeAsync()
    {
        var tenantId = Guid.CreateVersion7();
        var cashierId = Guid.CreateVersion7();

        await using (var owner = ScopedDbContext.WithoutTenant(postgres.ConnectionString))
        {
            owner.Db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Ledger Property Tenant",
                Slug = $"t-{tenantId:N}"[..20],
                CurrencyCode = "EUR",
                TimeZoneId = "Europe/Dublin",
                TaxMode = TaxMode.Exclusive,
            });

            await owner.Db.SaveChangesAsync();
        }

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, tenantId, cashierId);

        var catalog = await CatalogGraph.WriteAsync(scoped.Db);

        var register = new Register { Name = "Front Counter", IsActive = true };
        scoped.Db.Registers.Add(register);
        await scoped.Db.SaveChangesAsync();

        var shift = new Shift
        {
            RegisterId = register.Id,
            OpenedBy = cashierId,
            OpenedAt = DateTimeOffset.UtcNow,
            OpeningFloat = (Money)100m,
            Status = ShiftStatus.Open,
        };

        scoped.Db.Shifts.Add(shift);
        await scoped.Db.SaveChangesAsync();

        return new SaleWorld(tenantId, cashierId, register.Id, shift.Id, catalog.ProductId);
    }
}
