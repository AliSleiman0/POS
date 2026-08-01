using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>The ids of one tenant's seeded trading history.</summary>
public sealed record SeededSales(
    Guid ClosedShiftId,
    Guid OpenShiftId,
    Guid FirstSaleId,
    Guid SecondSaleId,
    Guid VoidedSaleId,
    Guid RefundSaleId,
    Guid DiscrepancyId)
{
    /// <summary>
    /// Everything <c>GET /sales</c> must return for this tenant.
    /// </summary>
    /// <remarks>
    /// All four, including the voided one and the refund. A history that quietly hid them is
    /// how a manager fails to find the transaction they are looking for and concludes the
    /// system lost it — so "returns everything" is the behaviour being pinned, not an
    /// accident of the fixture.
    /// </remarks>
    public IReadOnlyList<Guid> SaleIds => [FirstSaleId, SecondSaleId, VoidedSaleId, RefundSaleId];

    public IReadOnlyList<Guid> ShiftIds => [ClosedShiftId, OpenShiftId];

    public IReadOnlyList<Guid> DiscrepancyIds => [DiscrepancyId];
}

/// <summary>
/// A small, fixed trading history for the isolation world: two shifts, four sales and one
/// flagged oversell.
/// </summary>
/// <remarks>
/// <b>Seeded once and never written to again.</b> The isolation world's assertions are exact
/// set equalities, so a test that added a sale to either tenant would make every count in the
/// suite depend on the order xUnit happened to run the classes in. Behavioural sale and shift
/// tests use <c>RegisterSandbox</c> or a throwaway tenant instead.
/// <para>
/// Written through the DbContext rather than over HTTP, deliberately: the endpoints under test
/// must not also be the thing that builds the fixture, or a bug in them would produce a world
/// that agreed with itself.
/// </para>
/// <para>
/// Same principals-then-dependents ordering as <see cref="CatalogFixture"/>, and for the same
/// reason — there are still no navigation properties, so EF does no foreign-key fixup and an
/// id stays <c>Guid.Empty</c> until <c>SaveChanges</c> stamps it.
/// </para>
/// </remarks>
internal static class SalesFixture
{
    public static async Task<SeededSales> WriteAsync(
        PosApiFactory factory,
        Guid tenantId,
        Guid registerId,
        Guid cashierId,
        SeededCatalog catalog)
    {
        SeededSales? seeded = null;

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var opened = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero);

            // Pass 1: the shifts. A closed one and an open one, so a filter on status has
            // something to get wrong.
            var closed = new Shift
            {
                RegisterId = registerId,
                OpenedBy = cashierId,
                OpenedAt = opened,
                OpeningFloat = (Money)100m,
                Status = ShiftStatus.Closed,
                ClosedBy = cashierId,
                ClosedAt = opened.AddHours(8),
                CountedCash = (Money)150m,
                ExpectedCash = (Money)148.75m,
                Variance = (Money)1.25m,
            };

            var open = new Shift
            {
                RegisterId = registerId,
                OpenedBy = cashierId,
                OpenedAt = opened.AddDays(1),
                OpeningFloat = (Money)100m,
                Status = ShiftStatus.Open,
            };

            db.Shifts.AddRange(closed, open);
            await db.SaveChangesAsync();

            // Pass 2: the sales.
            var first = NewSale(1, closed.Id, registerId, cashierId, opened.AddHours(1), (Money)24.60m);
            var second = NewSale(2, closed.Id, registerId, cashierId, opened.AddHours(2), (Money)12.30m);

            var voided = NewSale(3, closed.Id, registerId, cashierId, opened.AddHours(3), (Money)6.15m);
            voided.Status = SaleStatus.Voided;
            voided.VoidedBy = cashierId;
            voided.VoidedAt = opened.AddHours(4);
            voided.VoidReason = "Rung up twice";

            db.Sales.AddRange(first, second, voided);
            await db.SaveChangesAsync();

            // Pass 3: the refund, which points at a sale that now has an id.
            var refund = NewSale(4, closed.Id, registerId, cashierId, opened.AddHours(5), (Money)(-12.30m));
            refund.Type = SaleType.Refund;
            refund.OriginalSaleId = second.Id;
            refund.RefundReason = "Faulty";

            db.Sales.Add(refund);
            await db.SaveChangesAsync();

            // Pass 4: lines, tenders and the discrepancy, all of which need a sale id.
            var line = new SaleLine
            {
                SaleId = first.Id,
                ProductId = catalog.WaterProductId,
                LineNumber = 1,
                Description = CatalogFixture.WaterName,
                Quantity = 2m,
                UnitPrice = (Money)10m,
                TaxRate = 0.2300m,
                DiscountAmount = Money.Zero,
                LineSubtotal = (Money)20m,
                LineTax = (Money)4.60m,
                LineTotal = (Money)24.60m,
            };

            db.SaleLines.Add(line);

            db.Tenders.Add(new Tender
            {
                SaleId = first.Id,
                Method = TenderMethod.Cash,
                Amount = (Money)25m,
                ChangeGiven = (Money)0.40m,
            });

            await db.SaveChangesAsync();

            var discrepancy = new StockDiscrepancy
            {
                ProductId = catalog.WaterProductId,
                SaleId = first.Id,
                SaleLineId = line.Id,
                QuantityRequested = 2m,
                OnHandAfter = -1m,
                DetectedAt = opened.AddHours(1),
            };

            db.StockDiscrepancies.Add(discrepancy);

            // The counter has to agree with the sale numbers above, or the first sale this
            // tenant writes through the API collides on ux_sale_tenant_sale_number — a failure
            // that would look like a bug in the endpoint rather than in the fixture.
            db.SaleSequences.Add(new SaleSequence { LastNumber = 4 });

            await db.SaveChangesAsync();

            seeded = new SeededSales(
                closed.Id, open.Id, first.Id, second.Id, voided.Id, refund.Id, discrepancy.Id);
        });

        return seeded!;
    }

    private static Sale NewSale(
        long number,
        Guid shiftId,
        Guid registerId,
        Guid cashierId,
        DateTimeOffset completedAt,
        Money total) =>
        new()
        {
            SaleNumber = number,
            ClientTransactionId = Guid.CreateVersion7(),
            RegisterId = registerId,
            ShiftId = shiftId,
            CashierId = cashierId,
            Type = SaleType.Sale,
            Status = SaleStatus.Completed,
            TaxMode = TaxMode.Exclusive,
            Subtotal = total / 1.23m,
            DiscountTotal = Money.Zero,
            TaxTotal = total - (total / 1.23m),
            RoundingAdjustment = Money.Zero,
            Total = total,
            CompletedAt = completedAt,
        };
}
