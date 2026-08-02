using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// Properties that must hold for <i>every</i> cart, checked over a few hundred random ones.
/// </summary>
/// <remarks>
/// These exist because the failures they catch are invisible to hand-picked examples. A
/// cent lost to apportionment shows up on baskets with an awkward ratio between the lines,
/// and nobody picks an awkward ratio on purpose — the example you write is the example that
/// works.
/// <para>
/// Seeded, so a failure is reproducible rather than a flake somebody reruns until it passes.
/// The seed is a constant in the source, and the counterexample is printed with the failure.
/// Hand-rolled rather than FsCheck: the suite has one testing idiom and this matches
/// <c>StockLedgerTests</c>' existing randomised loop.
/// </para>
/// </remarks>
public sealed class PricingPropertyTests
{
    private const int Seed = 20260802;

    private const int Carts = 500;

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void Apportioned_discounts_always_sum_to_the_discount_given(TaxMode mode)
    {
        ForEachCart(mode, (cart, sale) =>
        {
            if (cart.CartDiscount.IsZero)
            {
                return;
            }

            // Exactly, not nearly. A basket that apportions to 4.99 of a 5.00 promise is a
            // cent the shop gave away and cannot account for.
            var apportioned = Money.Sum(sale.Lines.Select(line => line.CartDiscountShare));

            Assert.Equal(cart.CartDiscount, apportioned);
        });
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void No_apportioned_share_ever_exceeds_its_line(TaxMode mode)
    {
        ForEachCart(mode, (cart, sale) =>
        {
            for (var index = 0; index < sale.Lines.Count; index++)
            {
                var line = sale.Lines[index];
                var net = line.Gross - cart.Lines[index].LineDiscount;

                // A share bigger than the line would make the line negative, which reads as
                // a refund embedded in a sale and reconciles against nothing.
                Assert.True(
                    line.CartDiscountShare <= net,
                    $"Line {line.LineNumber} took {line.CartDiscountShare} of a {net} line.");

                Assert.False(line.CartDiscountShare.IsNegative);
            }
        });
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void The_stored_totals_satisfy_invariant_one_for_every_random_cart(TaxMode mode)
    {
        // The property that guards the derived Subtotal. If someone "simplifies" it back to a
        // fourth independently rounded sum, this is the only thing that notices — and it
        // notices only because the carts are random. Every hand-written cart passes.
        ForEachCart(mode, (_, sale) => Assert.Equal(
            sale.Total,
            sale.Subtotal - sale.DiscountTotal + sale.TaxTotal + sale.RoundingAdjustment));
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void Every_stored_amount_fits_the_column_it_is_stored_in(TaxMode mode)
    {
        // Full precision is carried through the pipeline, but what reaches numeric(19,4) has
        // to survive it exactly — otherwise Postgres rounds silently and the stored sale is
        // not the sale that was computed.
        ForEachCart(mode, (_, sale) =>
        {
            Assert.True(sale.Subtotal.IsStorable);
            Assert.True(sale.DiscountTotal.IsStorable);
            Assert.True(sale.TaxTotal.IsStorable);
            Assert.True(sale.RoundingAdjustment.IsStorable);
            Assert.True(sale.Total.IsStorable);

            foreach (var line in sale.Lines)
            {
                Assert.True(line.Gross.IsStorable);
                Assert.True(line.Subtotal.IsStorable);
                Assert.True(line.Discount.IsStorable);
                Assert.True(line.Tax.IsStorable);
                Assert.True(line.Total.IsStorable);
            }
        });
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void A_total_is_never_negative_and_never_exceeds_the_undiscounted_basket(TaxMode mode)
    {
        ForEachCart(mode, (_, sale) =>
        {
            Assert.False(sale.Total.IsNegative);

            // A discount can only reduce. The ceiling is the gross plus whatever tax an
            // exclusive-mode basket adds on top of it, and cash rounding can nudge it by at
            // most one increment.
            var gross = Money.Sum(sale.Lines.Select(line => line.Gross));
            var ceiling = (gross + sale.TaxTotal).Round() + (Money)0.05m;

            Assert.True(sale.Total <= ceiling, $"Total {sale.Total} exceeded {ceiling}.");
        });
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void Pricing_the_same_cart_twice_gives_the_same_answer(TaxMode mode)
    {
        // Determinism, stated as a property. It is what makes POST /sales/quote meaningful:
        // the quote a customer was shown and the sale that follows are the same cart, and
        // any tie-break that depended on ordering or on a hash would show up here.
        //
        // Compared field by field, not with Assert.Equal on the record: PricedSale holds its
        // lines in an IReadOnlyList, and the compiler-generated record equality compares that
        // with EqualityComparer<T>.Default — reference equality for an array. Two runs
        // produce two arrays, so the record comparison fails on identical values.
        ForEachCart(mode, (cart, sale) =>
        {
            var again = PricingEngine.Price(cart);

            Assert.Equal(sale.Subtotal, again.Subtotal);
            Assert.Equal(sale.DiscountTotal, again.DiscountTotal);
            Assert.Equal(sale.TaxTotal, again.TaxTotal);
            Assert.Equal(sale.RoundingAdjustment, again.RoundingAdjustment);
            Assert.Equal(sale.Total, again.Total);
            Assert.Equal(sale.Lines, again.Lines);
        });
    }

    /// <summary>
    /// Prices <see cref="Carts"/> random carts and hands each to <paramref name="assert"/>,
    /// reporting the cart itself when one fails.
    /// </summary>
    private static void ForEachCart(TaxMode mode, Action<Cart, PricedSale> assert)
    {
        var random = new Random(Seed);

        for (var iteration = 0; iteration < Carts; iteration++)
        {
            var cart = NextCart(random, mode);

            PricedSale sale;

            try
            {
                sale = PricingEngine.Price(cart);
            }
            catch (InvalidDiscountException)
            {
                // A refused cart is a legitimate outcome, not a failed property. The
                // generator aims below the limit but a fully-zero basket can still ask for a
                // discount, and that is the case the engine is right to reject.
                continue;
            }

            try
            {
                assert(cart, sale);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    $"Cart {iteration} (seed {Seed}, {mode}) failed: {Describe(cart)}",
                    exception);
            }
        }
    }

    private static Cart NextCart(Random random, TaxMode mode)
    {
        var lines = new List<CartLine>();

        // Awkward on purpose: prices with real fourth decimals, fractional quantities, rates
        // that do not divide evenly, and line counts up to six so apportionment has enough
        // parts to lose a cent between.
        var lineCount = random.Next(1, 7);

        for (var index = 0; index < lineCount; index++)
        {
            var unitPrice = Math.Round((decimal)(random.NextDouble() * 40d), 4);
            var quantity = random.Next(0, 4) == 0
                ? Math.Round((decimal)(random.NextDouble() * 3d), 3)   // weighed
                : random.Next(1, 5);                                   // counted

            var gross = unitPrice * quantity;

            var lineDiscount = random.Next(0, 3) == 0
                ? Math.Round(gross * (decimal)random.NextDouble(), 4)
                : 0m;

            lines.Add(new CartLine(
                ProductId: Guid.CreateVersion7(),
                Description: $"Line {index + 1}",
                Quantity: quantity,
                UnitPrice: (Money)unitPrice,
                TaxRate: Rates[random.Next(Rates.Length)],
                LineDiscount: (Money)lineDiscount));
        }

        var netTotal = lines.Aggregate(
            0m, (running, line) => running + (line.UnitPrice.ToDecimal() * line.Quantity) - line.LineDiscount.ToDecimal());

        // Up to the whole basket, including exactly the whole basket often enough to exercise
        // the comped path rather than only the proportional one.
        //
        // Rounded to the storage scale in every branch, because that is the only kind of
        // discount the API can accept: cartDiscountAmount is a client-supplied value bound
        // for a numeric(19,4) column, and CatalogRules.IsStorableAmount refuses anything
        // finer at the endpoint. Generating an unstorable discount would test the engine
        // against a request that cannot exist, and the property would be asserting that
        // rounded shares sum to an unrounded whole — which is not true and should not be.
        var cartDiscount = random.Next(0, 3) switch
        {
            0 => 0m,
            1 => Rounding.To(netTotal * (decimal)random.NextDouble(), Rounding.StorageScale),
            _ => Rounding.To(netTotal, Rounding.StorageScale),
        };

        // Rounding the whole basket up would put the discount above it, which the engine is
        // right to refuse; nudge it back so the comped path is exercised instead of skipped.
        cartDiscount = Math.Min(cartDiscount, Rounding.To(netTotal, Rounding.StorageScale));

        var increment = random.Next(0, 4) == 0 ? 0.05m : 0m;

        return new Cart(lines, (Money)cartDiscount, mode, increment);
    }

    /// <summary>Rates that do not divide evenly, so the arithmetic has somewhere to go wrong.</summary>
    private static readonly decimal[] Rates = [0m, 0.05m, 0.09m, 0.135m, 0.21m, 0.23m, 1m];

    private static string Describe(Cart cart) =>
        $"discount={cart.CartDiscount}, rounding={cart.CashRoundingIncrement}, lines=["
        + string.Join(
            "; ",
            cart.Lines.Select(line =>
                $"{line.Quantity}×{line.UnitPrice}@{line.TaxRate}-{line.LineDiscount}"))
        + "]";
}
