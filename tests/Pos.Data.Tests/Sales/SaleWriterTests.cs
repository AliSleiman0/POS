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
/// The writer's own guards, tested without the endpoint in front of them.
/// </summary>
/// <remarks>
/// <b>Why these are not redundant with the API tests.</b> <c>POST /sales</c> validates the
/// shift before calling the writer, so an endpoint test cannot tell whether the writer's check
/// does anything — deleting it leaves the whole API suite green. That was found by deleting
/// it. The endpoint's check exists to produce a good error message; the writer's exists to be
/// the guarantee, under the row lock, where a shift closing concurrently is decided.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class SaleWriterTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_sale_against_a_closed_shift_is_refused_by_the_writer()
    {
        var world = await ArrangeAsync(shiftStatus: ShiftStatus.Closed);

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, world.TenantId, world.CashierId);

        var writer = scoped.Services.GetRequiredService<ISaleWriter>();

        await Assert.ThrowsAsync<Core.Exceptions.ShiftClosedException>(
            () => writer.CommitAsync(Request(world), onCommitting: null, CancellationToken.None));

        await AssertNothingCommittedAsync(world.TenantId);
    }

    [Fact]
    public async Task A_sale_naming_another_registers_shift_is_refused_by_the_writer()
    {
        var world = await ArrangeAsync();

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, world.TenantId, world.CashierId);

        var writer = scoped.Services.GetRequiredService<ISaleWriter>();

        // The shift is open, but on a different till. Refused here as well as at the endpoint,
        // because a sale attributed to the wrong drawer makes a Z-report reconcile the wrong
        // till and the writer is the last place that can still say no.
        var request = Request(world) with { RegisterId = Guid.CreateVersion7() };

        await Assert.ThrowsAsync<Core.Exceptions.ShiftClosedException>(
            () => writer.CommitAsync(request, onCommitting: null, CancellationToken.None));

        await AssertNothingCommittedAsync(world.TenantId);
    }

    [Fact]
    public async Task A_committed_sale_writes_its_number_lines_tenders_and_movements()
    {
        // The positive control. Without it the two refusals above would pass against a writer
        // that refused everything.
        var world = await ArrangeAsync();

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, world.TenantId, world.CashierId);

        var writer = scoped.Services.GetRequiredService<ISaleWriter>();

        var result = await writer.CommitAsync(Request(world), onCommitting: null, CancellationToken.None);

        Assert.Equal(1, result.SaleNumber);
        Assert.Single(result.LineIds);
        Assert.Empty(result.Discrepancies);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        Assert.Single(await reader.Db.Sales.ToListAsync());
        Assert.Single(await reader.Db.SaleLines.ToListAsync());
        Assert.Single(await reader.Db.Tenders.ToListAsync());

        var movement = await reader.Db.StockMovements.SingleAsync(m => m.SaleId == result.SaleId);

        Assert.Equal(StockMovementType.Sale, movement.Type);
        Assert.Equal(-1m, movement.Quantity);

        // The cashier came from the current actor, not from the request — there is nowhere in
        // SaleCommitRequest to put one.
        var sale = await reader.Db.Sales.SingleAsync();
        Assert.Equal(world.CashierId, sale.CashierId);
    }

    [Fact]
    public async Task The_callback_runs_inside_the_transaction_and_its_writes_commit_with_it()
    {
        // What makes "the idempotency key lands in the same transaction as the work" true
        // rather than approximately true.
        var world = await ArrangeAsync();

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, world.TenantId, world.CashierId);

        var writer = scoped.Services.GetRequiredService<ISaleWriter>();

        var result = await writer.CommitAsync(
            Request(world),
            committed => scoped.Db.IdempotencyRecords.Add(new IdempotencyRecord
            {
                Key = Guid.CreateVersion7(),
                Endpoint = "POST /api/v1/sales",
                RequestHash = new string('a', 64),
                ResponseStatus = 201,
                ResponseBody = $$"""{"saleNumber":{{committed.SaleNumber}}}""",
                CompletedAt = committed.CompletedAt,
            }),
            CancellationToken.None);

        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);

        var record = await reader.Db.IdempotencyRecords.SingleAsync();

        Assert.Contains(
            $"\"saleNumber\":{result.SaleNumber}",
            record.ResponseBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failure_inside_the_callback_rolls_the_whole_sale_back()
    {
        // Forced, rather than hoped for. By the time the callback runs the sale, its lines,
        // its tenders and its stock movements are all written — so if this were not one
        // transaction, a sale would survive with no idempotency record and a decremented
        // stock figure behind it.
        var world = await ArrangeAsync();

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, world.TenantId, world.CashierId);

        var writer = scoped.Services.GetRequiredService<ISaleWriter>();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.CommitAsync(
                Request(world),
                _ => throw new InvalidOperationException("Forced."),
                CancellationToken.None));

        await AssertNothingCommittedAsync(world.TenantId);

        // And the counter did not advance, so the next real sale is still number 1. A
        // sequence would have burned the number permanently.
        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, world.TenantId);
        Assert.Empty(await reader.Db.SaleSequences.ToListAsync());
    }

    private static SaleCommitRequest Request(SaleWorld world)
    {
        var cart = new Cart(
            [
                new CartLine(
                    world.Catalog.ProductId,
                    "Still Water 500ml",
                    Quantity: 1m,
                    UnitPrice: (Money)1.2000m,
                    TaxRate: 0.2300m,
                    LineDiscount: Money.Zero),
            ],
            Money.Zero,
            TaxMode.Exclusive);

        var priced = PricingEngine.Price(cart);

        return new SaleCommitRequest(
            Guid.CreateVersion7(),
            world.RegisterId,
            world.ShiftId,
            TaxMode.Exclusive,
            priced,
            [new TenderInstruction(TenderMethod.Cash, (Money)5m)],
            new HashSet<Guid> { world.Catalog.ProductId });
    }

    private async Task AssertNothingCommittedAsync(Guid tenantId)
    {
        await using var reader = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        Assert.Empty(await reader.Db.Sales.ToListAsync());
        Assert.Empty(await reader.Db.SaleLines.ToListAsync());
        Assert.Empty(await reader.Db.Tenders.ToListAsync());
        Assert.Empty(await reader.Db.StockMovements.Where(m => m.SaleId != null).ToListAsync());
    }

    private sealed record SaleWorld(
        Guid TenantId,
        Guid CashierId,
        Guid RegisterId,
        Guid ShiftId,
        SeededCatalog Catalog);

    private async Task<SaleWorld> ArrangeAsync(ShiftStatus shiftStatus = ShiftStatus.Open)
    {
        var tenantId = Guid.CreateVersion7();
        var cashierId = Guid.CreateVersion7();

        await using (var owner = ScopedDbContext.WithoutTenant(postgres.ConnectionString))
        {
            owner.Db.Tenants.Add(new Tenant
            {
                Id = tenantId,
                Name = "Sale Writer Tenant",
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
            Status = shiftStatus,
        };

        scoped.Db.Shifts.Add(shift);
        await scoped.Db.SaveChangesAsync();

        return new SaleWorld(tenantId, cashierId, register.Id, shift.Id, catalog);
    }
}
