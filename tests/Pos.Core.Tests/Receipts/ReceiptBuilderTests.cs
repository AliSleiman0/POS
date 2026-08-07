using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;
using Pos.Core.Receipts;

namespace Pos.Core.Tests.Receipts;

/// <summary>
/// The receipt payload, built from a stored sale and nothing else.
/// </summary>
/// <remarks>
/// Two things are being pinned here, and only one of them is obvious. The obvious one is that
/// the right values reach the right fields. The other is that the <b>tax breakdown adds up</b>:
/// line amounts are stored at four decimal places and the sale header is rounded to two, so a
/// breakdown that rounds each rate's group independently misses the sale's own tax total by a
/// cent on some baskets and not others — and passes every example anybody writes by hand.
/// </remarks>
public sealed class ReceiptBuilderTests
{
    /// <summary>UTC+02:00, invented rather than looked up, so the assertions do not depend on
    /// the machine's zone database.</summary>
    private static readonly TimeZoneInfo PlusTwo = TimeZoneInfo.CreateCustomTimeZone(
        "Test/PlusTwo", TimeSpan.FromHours(2), "Test +02:00", "Test +02:00");

    private static readonly DateTimeOffset CompletedAt =
        new(2026, 8, 7, 22, 15, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset IssuedAt =
        new(2026, 8, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_receipt_carries_the_shops_header_block_and_the_sales_own_totals()
    {
        var receipt = Build(Priced(
            Line(quantity: 3m, unitPrice: 4.9900m, taxRate: 0.2300m, description: "Merlot"),
            Line(quantity: 2m, unitPrice: 1.5000m, taxRate: 0.0000m, description: "Sourdough")));

        Assert.Equal(ReceiptKind.Sale, receipt.Kind);
        Assert.Equal("Corner Shop", receipt.Shop.Name);
        Assert.Equal("14 Harbour Road", receipt.Shop.AddressLine);
        Assert.Equal("IE1234567FA", receipt.Shop.TaxNumber);
        Assert.Equal("EUR", receipt.Shop.CurrencyCode);
        Assert.Equal("Ada Cashier", receipt.CashierName);
        Assert.Equal("Front Counter", receipt.RegisterName);

        Assert.Equal(2, receipt.Lines.Count);
        Assert.Equal("Merlot", receipt.Lines[0].Description);
        Assert.Equal(1, receipt.Lines[0].LineNumber);
        Assert.Equal(3m, receipt.Lines[0].Quantity);
        Assert.Equal(4.9900m, receipt.Lines[0].UnitPrice.ToDecimal());
    }

    /// <summary>
    /// §6.1's exit criterion, on the cart <c>HandCheckedCartTests</c> worked out on paper.
    /// </summary>
    /// <remarks>
    /// That cart is the awkward one deliberately — inclusive tax, two rates, and a cart
    /// discount that does not divide evenly — so it is the one whose breakdown is most likely
    /// to miss by a cent.
    /// </remarks>
    [Fact]
    public void The_tax_breakdown_is_by_rate_and_its_parts_sum_to_the_tax_total()
    {
        var sale = Priced(
            cartDiscount: 5.0000m,
            mode: TaxMode.Inclusive,
            Line(quantity: 3m, unitPrice: 4.9900m, taxRate: 0.2300m),
            Line(quantity: 2m, unitPrice: 1.5000m, taxRate: 0.0000m));

        var receipt = Build(sale);

        // Ascending by rate: zero-rated first, which is the order a person reads and the order
        // that fixes where a residue lands.
        Assert.Equal([0.0000m, 0.2300m], receipt.TaxBreakdown.Select(t => t.Rate).ToArray());

        Assert.Equal(
            receipt.TaxTotal,
            Money.Sum(receipt.TaxBreakdown.Select(t => t.TaxAmount)));

        // The taxable base reconciles too. A breakdown whose tax adds up while its net does not
        // shows a customer a VAT base that disagrees with the total printed underneath it.
        Assert.Equal(
            receipt.Subtotal - receipt.DiscountTotal,
            Money.Sum(receipt.TaxBreakdown.Select(t => t.NetAmount)));

        // 2.02 of tax on the standard-rated line, nothing on the zero-rated one — the figures
        // HandCheckedCartTests computed on paper.
        Assert.Equal(0m, receipt.TaxBreakdown[0].TaxAmount.ToDecimal());
        Assert.Equal(2.02m, receipt.TaxBreakdown[1].TaxAmount.ToDecimal());
    }

    /// <summary>
    /// The same two sums, over enough random baskets to find the ones nobody would write.
    /// </summary>
    /// <remarks>
    /// Seeded, like <c>PricingPropertyTests</c>, so a failure is reproducible rather than a
    /// flake somebody reruns. This is the test that would catch the breakdown being "simplified"
    /// to a per-group round with no residue step: every hand-written example still passes.
    /// </remarks>
    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void Every_random_baskets_breakdown_reconciles_with_its_own_header(TaxMode mode)
    {
        var random = new Random(20260807);

        for (var iteration = 0; iteration < 400; iteration++)
        {
            var lines = new List<CartLine>();
            var lineCount = random.Next(1, 7);

            for (var index = 0; index < lineCount; index++)
            {
                // Four rates, so most baskets are mixed and the breakdown has parts to lose a
                // cent between. Prices carry a real fourth decimal and some lines are weighed.
                var rate = new[] { 0.0000m, 0.0900m, 0.1350m, 0.2300m }[random.Next(4)];
                var unitPrice = Math.Round((decimal)(random.NextDouble() * 40d), 4);
                var quantity = random.Next(0, 4) == 0
                    ? Math.Round((decimal)(random.NextDouble() * 3d), 3)
                    : random.Next(1, 5);

                var gross = unitPrice * quantity;
                var discount = random.Next(0, 3) == 0
                    ? Math.Round(gross * (decimal)random.NextDouble(), 4)
                    : 0m;

                lines.Add(Line(quantity, unitPrice, rate, discount));
            }

            var basket = Money.Sum(lines.Select(l => (l.UnitPrice * l.Quantity) - l.LineDiscount));

            // Below the basket, so the engine prices it rather than refusing it. A refused cart
            // is a legitimate outcome of the generator, not a property failure.
            var cartDiscount = basket.IsZero || random.Next(0, 3) != 0
                ? 0m
                : Math.Round(basket.ToDecimal() * (decimal)(random.NextDouble() * 0.5d), 4);

            var receipt = Build(Priced(cartDiscount, mode, [.. lines]));

            try
            {
                Assert.Equal(
                    receipt.TaxTotal,
                    Money.Sum(receipt.TaxBreakdown.Select(t => t.TaxAmount)));

                Assert.Equal(
                    receipt.Subtotal - receipt.DiscountTotal,
                    Money.Sum(receipt.TaxBreakdown.Select(t => t.NetAmount)));

                // Gross follows from the other two, and equals what was payable before cash
                // rounding nudged it. Stated separately because it is the number a customer
                // checks the breakdown against.
                Assert.Equal(
                    receipt.Total - receipt.RoundingAdjustment,
                    Money.Sum(receipt.TaxBreakdown.Select(t => t.GrossAmount)));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Basket {iteration} ({mode}, {lineCount} lines, cart discount {cartDiscount}) "
                    + "produced a breakdown that does not reconcile.",
                    exception);
            }
        }
    }

    [Fact]
    public void A_sale_with_one_rate_still_gets_a_breakdown()
    {
        var receipt = Build(Priced(Line(quantity: 2m, unitPrice: 10m, taxRate: 0.2300m)));

        var only = Assert.Single(receipt.TaxBreakdown);

        Assert.Equal(0.2300m, only.Rate);
        Assert.Equal(receipt.TaxTotal, only.TaxAmount);
    }

    [Fact]
    public void A_zero_rated_sale_gets_a_zero_rated_line_rather_than_no_breakdown()
    {
        // A receipt with no tax section reads as one where tax was forgotten. "0% — €0.00" is
        // the statement a zero-rated basket is legally making.
        var receipt = Build(Priced(Line(quantity: 1m, unitPrice: 3m, taxRate: 0m)));

        var only = Assert.Single(receipt.TaxBreakdown);

        Assert.Equal(0m, only.Rate);
        Assert.True(only.TaxAmount.IsZero);
        Assert.Equal(3m, only.NetAmount.ToDecimal());
    }

    [Fact]
    public void Timestamps_are_in_the_tenants_zone_and_not_in_utc()
    {
        // 22:15 UTC is 00:15 the next day at +02:00. Printing the UTC time would put the sale
        // on the wrong date, which is the version of this bug that reaches a dispute.
        var receipt = Build(Priced(Line(1m, 5m, 0.2300m)));

        Assert.Equal(TimeSpan.FromHours(2), receipt.CompletedAtLocal.Offset);
        Assert.Equal(new DateTime(2026, 8, 8, 0, 15, 0), receipt.CompletedAtLocal.DateTime);
        Assert.Equal(new DateTime(2026, 8, 8, 11, 0, 0), receipt.IssuedAtLocal.DateTime);
        Assert.Equal("Test/PlusTwo", receipt.TimeZoneId);
    }

    [Fact]
    public void A_refund_says_what_it_is_and_names_the_sale_it_reverses()
    {
        var (sale, lines) = Stored(Priced(Line(1m, 10m, 0.2300m)));

        sale.Type = SaleType.Refund;
        sale.RefundReason = "Faulty";
        sale.Total = -sale.Total;

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(sale, lines, Tenders(), Shop(), "Ada Cashier", "Front Counter", 41),
            PlusTwo,
            IssuedAt);

        Assert.Equal(ReceiptKind.Refund, receipt.Kind);
        Assert.Equal(41, receipt.OriginalSaleNumber);
        Assert.Equal("Faulty", receipt.RefundReason);
    }

    [Fact]
    public void A_voided_sale_prints_as_a_void_with_its_reason()
    {
        var (sale, lines) = Stored(Priced(Line(1m, 10m, 0.2300m)));

        sale.Status = SaleStatus.Voided;
        sale.VoidReason = "Rung up twice";

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(sale, lines, Tenders(), Shop(), "Ada Cashier", "Front Counter", null),
            PlusTwo,
            IssuedAt);

        Assert.Equal(ReceiptKind.VoidedSale, receipt.Kind);
        Assert.Equal("Rung up twice", receipt.VoidReason);
    }

    [Fact]
    public void A_voided_refund_prints_as_a_void_rather_than_as_a_refund()
    {
        // Status before type: "this was reversed" is the fact the reader needs first, and the
        // one that must not be missable behind a refund heading.
        var (sale, lines) = Stored(Priced(Line(1m, 10m, 0.2300m)));

        sale.Type = SaleType.Refund;
        sale.Status = SaleStatus.Voided;
        sale.VoidReason = "Refunded in error";

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(sale, lines, Tenders(), Shop(), "Ada Cashier", "Front Counter", 41),
            PlusTwo,
            IssuedAt);

        Assert.Equal(ReceiptKind.VoidedSale, receipt.Kind);
    }

    [Fact]
    public void Change_is_summed_over_every_tender_rather_than_read_off_the_first()
    {
        var (sale, lines) = Stored(Priced(Line(1m, 10m, 0.2300m)));

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(
                sale,
                lines,
                [
                    new Tender { Method = TenderMethod.Cash, Amount = (Money)5m },
                    new Tender { Method = TenderMethod.Cash, Amount = (Money)10m, ChangeGiven = (Money)2.70m },
                ],
                Shop(),
                "Ada Cashier",
                "Front Counter",
                null),
            PlusTwo,
            IssuedAt);

        Assert.Equal(2, receipt.Tenders.Count);
        Assert.Equal(2.70m, receipt.ChangeGiven.ToDecimal());
    }

    [Fact]
    public void A_shop_that_has_filled_nothing_in_prints_nothing_rather_than_blank_lines()
    {
        var (sale, lines) = Stored(Priced(Line(1m, 10m, 0.2300m)));

        var shop = Shop();
        shop.AddressLine = null;
        shop.TaxNumber = "   ";
        shop.ReceiptHeader = null;
        shop.ReceiptFooter = "";

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(sale, lines, Tenders(), shop, "Ada Cashier", "Front Counter", null),
            PlusTwo,
            IssuedAt);

        Assert.Null(receipt.Shop.AddressLine);
        Assert.Null(receipt.Shop.TaxNumber);
        Assert.Null(receipt.Shop.Header);
        Assert.Null(receipt.Shop.Footer);
    }

    [Fact]
    public void Lines_come_back_in_the_order_they_were_rung_however_they_arrive()
    {
        var (sale, lines) = Stored(Priced(
            Line(1m, 5m, 0.2300m),
            Line(1m, 6m, 0.2300m),
            Line(1m, 7m, 0.2300m)));

        var receipt = ReceiptBuilder.Build(
            new ReceiptSource(
                sale,
                [.. lines.Reverse()],
                Tenders(),
                Shop(),
                "Ada Cashier",
                "Front Counter",
                null),
            PlusTwo,
            IssuedAt);

        Assert.Equal([1, 2, 3], receipt.Lines.Select(l => l.LineNumber).ToArray());
    }

    // ---- fixture -------------------------------------------------------------------

    private static Receipt Build(PricedSale priced)
    {
        var (sale, lines) = Stored(priced);

        return ReceiptBuilder.Build(
            new ReceiptSource(sale, lines, Tenders(), Shop(), "Ada Cashier", "Front Counter", null),
            PlusTwo,
            IssuedAt);
    }

    /// <summary>
    /// Materialises a priced cart into the rows a sale actually stores.
    /// </summary>
    /// <remarks>
    /// Mirrors <c>SaleWriter</c> field for field. Building the receipt from a hand-written
    /// <c>Sale</c> instead would let the test agree with itself while disagreeing with what the
    /// database holds — and the residue step only matters because the stored line amounts are
    /// rounded to four places while the header is rounded to two.
    /// </remarks>
    private static (Sale Sale, SaleLine[] Lines) Stored(PricedSale priced)
    {
        var sale = new Sale
        {
            SaleNumber = 42,
            ClientTransactionId = Guid.CreateVersion7(),
            Type = SaleType.Sale,
            Status = SaleStatus.Completed,
            TaxMode = TaxMode.Exclusive,
            Subtotal = priced.Subtotal,
            DiscountTotal = priced.DiscountTotal,
            TaxTotal = priced.TaxTotal,
            RoundingAdjustment = priced.RoundingAdjustment,
            Total = priced.Total,
            CompletedAt = CompletedAt,
        };

        var lines = priced.Lines
            .Select(line => new SaleLine
            {
                SaleId = sale.Id,
                ProductId = line.Source.ProductId,
                LineNumber = line.LineNumber,
                Description = line.Source.Description,
                Quantity = line.Source.Quantity,
                UnitPrice = line.Source.UnitPrice,
                TaxRate = line.Source.TaxRate,
                DiscountAmount = line.Discount,
                LineSubtotal = line.Subtotal,
                LineTax = line.Tax,
                LineTotal = line.Total,
                IsPriceOverridden = line.Source.IsPriceOverridden,
            })
            .ToArray();

        return (sale, lines);
    }

    private static PricedSale Priced(params CartLine[] lines) =>
        Priced(0m, TaxMode.Exclusive, lines);

    private static PricedSale Priced(decimal cartDiscount, TaxMode mode, params CartLine[] lines) =>
        PricingEngine.Price(new Cart(lines, (Money)cartDiscount, mode));

    private static CartLine Line(
        decimal quantity,
        decimal unitPrice,
        decimal taxRate,
        decimal lineDiscount = 0m,
        string description = "Item") =>
        new(
            ProductId: Guid.CreateVersion7(),
            Description: description,
            Quantity: quantity,
            UnitPrice: (Money)unitPrice,
            TaxRate: taxRate,
            LineDiscount: (Money)lineDiscount,
            IsPriceOverridden: false);

    private static Tender[] Tenders() =>
        [new Tender { Method = TenderMethod.Cash, Amount = (Money)50m, ChangeGiven = (Money)1m }];

    private static Tenant Shop() => new()
    {
        Name = "Corner Shop",
        Slug = "corner-shop",
        CurrencyCode = "EUR",
        TimeZoneId = "Test/PlusTwo",
        AddressLine = "14 Harbour Road",
        TaxNumber = "IE1234567FA",
        ReceiptHeader = "Open 7 days",
        ReceiptFooter = "Thank you",
    };
}
