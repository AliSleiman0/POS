using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// The carts every pricing test — on either side of the wire — is asserted against.
/// </summary>
/// <remarks>
/// <b>Why this is shared source rather than two generators.</b> Phase 9 puts a second
/// implementation of the pricing rules in the browser, because an offline till has no
/// <c>POST /sales/quote</c> to ask and still has to tell a customer what they owe. Two
/// implementations of tax and discount rules will differ eventually; what keeps these two
/// honest is that both are asserted against one corpus, generated from here and committed as
/// <c>tests/fixtures/pricing-conformance.json</c>.
/// <para>
/// So the carts have to be the same carts. A TypeScript generator writing "equivalent" random
/// baskets would drift from this one on the first change to either, and the corpus would quietly
/// stop comparing the thing it was built to compare.
/// </para>
/// <para>
/// Seeded and deterministic: the same 500 carts per mode, in the same order, on every machine
/// and every run. That is what makes the committed fixture reviewable — a change to the engine
/// shows up as a diff in the expected amounts, and a change to the <i>generator</i> shows up as
/// a diff in the inputs, which are two very different reviews.
/// </para>
/// </remarks>
public static class PricingCorpus
{
    /// <summary>The seed. A constant in source, so a failure is reproducible.</summary>
    public const int Seed = 20260802;

    /// <summary>Random carts per tax mode.</summary>
    public const int RandomCarts = 500;

    /// <summary>Rates that do not divide evenly, so the arithmetic has somewhere to go wrong.</summary>
    private static readonly decimal[] Rates = [0m, 0.05m, 0.09m, 0.135m, 0.21m, 0.23m, 1m];

    /// <summary>
    /// The hand-worked carts, first, so the corpus opens with the cases a person checked on
    /// paper rather than with cart 0 of a pseudo-random sequence.
    /// </summary>
    /// <remarks>
    /// These are the two from <see cref="HandCheckedCartTests"/> — the same basket priced both
    /// ways — plus the boundaries that hand-picked examples usually miss: an empty-ish line, a
    /// fully comped basket, a 100% rate, and a cash-rounding increment that actually bites.
    /// <para>
    /// Product ids are fixed rather than generated. The corpus is committed, and a file whose
    /// every line changes on each regeneration cannot be reviewed.
    /// </para>
    /// </remarks>
    public static IEnumerable<(string Name, Cart Cart)> HandChecked()
    {
        yield return ("hand-checked-inclusive", new Cart(
            [
                Line(1, quantity: 3m, unitPrice: 4.9900m, taxRate: 0.2300m),
                Line(2, quantity: 2m, unitPrice: 1.5000m, taxRate: 0.0000m),
            ],
            CartDiscount: (Money)5.0000m,
            TaxMode: TaxMode.Inclusive));

        yield return ("hand-checked-exclusive", new Cart(
            [
                Line(1, quantity: 3m, unitPrice: 4.9900m, taxRate: 0.2300m),
                Line(2, quantity: 2m, unitPrice: 1.5000m, taxRate: 0.0000m),
            ],
            CartDiscount: (Money)5.0000m,
            TaxMode: TaxMode.Exclusive));

        // The sale Phase 8 actually rang against production: €1.20 inclusive at 23%,
        // which is €0.98 + €0.22. If anything in this corpus has to keep working, it is this.
        yield return ("production-smoke-sale", new Cart(
            [Line(1, quantity: 1m, unitPrice: 1.2000m, taxRate: 0.2300m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Inclusive));

        // A single line, no discount, no tax: the simplest thing a till ever sells, and the
        // one a broken engine is most likely to still get right. Here as a control.
        yield return ("single-untaxed-line", new Cart(
            [Line(1, quantity: 1m, unitPrice: 10.0000m, taxRate: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        // Free gift: a zero-price line priced without complaint.
        yield return ("zero-price-line", new Cart(
            [
                Line(1, quantity: 1m, unitPrice: 0m, taxRate: 0.2300m),
                Line(2, quantity: 1m, unitPrice: 5.0000m, taxRate: 0.2300m),
            ],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive));

        // The comped path: the discount is exactly the basket, so every share is its whole
        // line and the proportional branch is never taken.
        yield return ("fully-comped", new Cart(
            [
                Line(1, quantity: 3m, unitPrice: 3.3333m, taxRate: 0.2100m),
                Line(2, quantity: 1m, unitPrice: 1.1111m, taxRate: 0.0500m),
            ],
            CartDiscount: (Money)11.1110m,
            TaxMode: TaxMode.Exclusive));

        // A 100% rate, which is legal if degenerate: inclusive, exactly half the shelf price
        // is tax. The divisor is 2 and nothing special-cases it.
        yield return ("hundred-percent-inclusive", new Cart(
            [Line(1, quantity: 1m, unitPrice: 10.0000m, taxRate: 1m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Inclusive));

        // Cash rounding that bites in both directions, one cart each.
        yield return ("cash-rounding-down", new Cart(
            [Line(1, quantity: 1m, unitPrice: 4.9700m, taxRate: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive,
            CashRoundingIncrement: 0.05m));

        yield return ("cash-rounding-up", new Cart(
            [Line(1, quantity: 1m, unitPrice: 4.9800m, taxRate: 0m)],
            CartDiscount: Money.Zero,
            TaxMode: TaxMode.Exclusive,
            CashRoundingIncrement: 0.05m));

        // A weighed line with a price whose fourth decimal matters, and a line discount that
        // does not divide evenly — the shape the property carts explore randomly.
        yield return ("weighed-with-line-discount", new Cart(
            [
                Line(1, quantity: 0.3330m, unitPrice: 12.9900m, taxRate: 0.1350m, lineDiscount: 1.2345m),
                Line(2, quantity: 7m, unitPrice: 0.1650m, taxRate: 0.0900m),
            ],
            CartDiscount: (Money)0.7700m,
            TaxMode: TaxMode.Inclusive));

        // Six lines, mixed rates, a cart discount with an awkward ratio: the apportionment
        // remainder has somewhere to land and a wrong tie-break shows up.
        yield return ("six-line-mixed-rates", new Cart(
            [
                Line(1, quantity: 1m, unitPrice: 1.0100m, taxRate: 0.2300m),
                Line(2, quantity: 2m, unitPrice: 2.0200m, taxRate: 0.1350m),
                Line(3, quantity: 3m, unitPrice: 3.0300m, taxRate: 0.0900m),
                Line(4, quantity: 4m, unitPrice: 4.0400m, taxRate: 0.0500m),
                Line(5, quantity: 5m, unitPrice: 5.0500m, taxRate: 0m),
                Line(6, quantity: 6m, unitPrice: 6.0600m, taxRate: 0.2100m),
            ],
            CartDiscount: (Money)7.7700m,
            TaxMode: TaxMode.Inclusive,
            CashRoundingIncrement: 0.05m));
    }

    /// <summary>
    /// Every cart in the corpus: the hand-worked ones, then the seeded random ones per mode.
    /// </summary>
    /// <remarks>
    /// Carts the engine legitimately refuses are <b>skipped</b>, not included with an expected
    /// error. A refused cart is a real outcome and the TypeScript port refuses the same ones,
    /// but a corpus of expected amounts is the wrong place to assert it — those cases have
    /// their own tests on both sides.
    /// </remarks>
    public static IEnumerable<(string Name, Cart Cart)> All()
    {
        foreach (var entry in HandChecked())
        {
            yield return entry;
        }

        foreach (var mode in new[] { TaxMode.Exclusive, TaxMode.Inclusive })
        {
            var random = new Random(Seed);

            for (var iteration = 0; iteration < RandomCarts; iteration++)
            {
                var cart = NextCart(random, mode, iteration);

                if (!IsPriceable(cart))
                {
                    continue;
                }

                yield return ($"random-{mode.ToString().ToLowerInvariant()}-{iteration:D3}", cart);
            }
        }
    }

    /// <summary>Whether the engine accepts this cart at all.</summary>
    /// <remarks>
    /// The generator aims below the discount limit, but a basket that prices to zero can still
    /// ask for one, and that is the case the engine is right to reject.
    /// </remarks>
    public static bool IsPriceable(Cart cart)
    {
        try
        {
            PricingEngine.Price(cart);
            return true;
        }
        catch (Core.Exceptions.InvalidDiscountException)
        {
            return false;
        }
    }

    /// <summary>
    /// One random cart. <b>Do not change this without regenerating the fixture</b> — the
    /// committed corpus is these carts, and a generator change is a change to the inputs.
    /// </summary>
    public static Cart NextCart(Random random, TaxMode mode, int iteration)
    {
        ArgumentNullException.ThrowIfNull(random);

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

            lines.Add(Line(
                index + 1,
                quantity,
                unitPrice,
                Rates[random.Next(Rates.Length)],
                lineDiscount,
                iteration));
        }

        var netTotal = lines.Aggregate(
            0m,
            (running, line) =>
                running + (line.UnitPrice.ToDecimal() * line.Quantity) - line.LineDiscount.ToDecimal());

        // Up to the whole basket, including exactly the whole basket often enough to exercise
        // the comped path rather than only the proportional one.
        //
        // Rounded to the storage scale in every branch, because that is the only kind of
        // discount the API can accept: cartDiscountAmount is a client-supplied value bound for
        // a numeric(19,4) column, and CatalogRules.IsStorableAmount refuses anything finer at
        // the endpoint.
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

    /// <summary>
    /// A line with a <b>stable</b> product id.
    /// </summary>
    /// <remarks>
    /// Derived from the line's position rather than generated, because the corpus is committed
    /// and a file whose every id changes on each regeneration cannot be reviewed — the diff
    /// that matters would be buried in five hundred new GUIDs.
    /// </remarks>
    private static CartLine Line(
        int number,
        decimal quantity,
        decimal unitPrice,
        decimal taxRate,
        decimal lineDiscount = 0m,
        int iteration = 0) =>
        new(
            ProductId: StableId(iteration, number),
            Description: $"Line {number}",
            Quantity: quantity,
            UnitPrice: (Money)unitPrice,
            TaxRate: taxRate,
            LineDiscount: (Money)lineDiscount);

    private static Guid StableId(int iteration, int number)
    {
        var bytes = new byte[16];
        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), iteration);
        BitConverter.TryWriteBytes(bytes.AsSpan(4, 4), number);

        return new Guid(bytes);
    }
}
