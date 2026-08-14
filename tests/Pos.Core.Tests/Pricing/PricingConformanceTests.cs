using System.Text.Json;
using System.Text.Json.Serialization;
using Pos.Core.Entities;
using Pos.Core.Pricing;

namespace Pos.Core.Tests.Pricing;

/// <summary>
/// The corpus both pricing engines are pinned to.
/// </summary>
/// <remarks>
/// Phase 9 puts a second implementation of these rules in the browser, because an offline till
/// has no <c>POST /sales/quote</c> to ask and still has to tell a customer what they owe. That
/// is a deliberate amendment to CLAUDE.md invariant 3, and this file is the thing that makes it
/// survivable: <b>one committed corpus, asserted from both sides</b>.
/// <list type="bullet">
/// <item>This class asserts <c>PricingEngine.Price</c> reproduces the fixture.</item>
/// <item><c>src/Pos.Web/src/lib/pricing/pricing.conformance.test.ts</c> asserts the TypeScript
/// port reproduces the same file.</item>
/// </list>
/// A change to either engine that is not a change to both turns one of them red in CI, which is
/// the only mechanism that keeps two implementations of the same money rules honest.
/// <para>
/// <b>Regenerating is deliberate, not automatic.</b> Set <c>POS_REGENERATE_PRICING_CORPUS=1</c>
/// and run this test; it rewrites the fixture and fails, so a regeneration can never be
/// mistaken for a passing run. The rewritten file is then a reviewable diff — which is the
/// point. An engine change that moves a hundred totals should look like a hundred moved totals
/// in a pull request, not like a green tick.
/// </para>
/// </remarks>
public sealed class PricingConformanceTests
{
    /// <summary>Set to <c>1</c> to rewrite the fixture. The test still fails afterwards.</summary>
    private const string RegenerateVariable = "POS_REGENERATE_PRICING_CORPUS";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [Fact]
    public void The_engine_reproduces_every_cart_in_the_committed_corpus()
    {
        var actual = Generate();

        // Before reading the fixture, not after: reading asserts the file exists, so a check
        // that ran second could never create it the first time.
        if (Environment.GetEnvironmentVariable(RegenerateVariable) == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FixturePath)!);
            File.WriteAllText(FixturePath, JsonSerializer.Serialize(actual, Json));

            Assert.Fail(
                $"The corpus was regenerated at {FixturePath}. Review the diff and re-run "
                + $"without {RegenerateVariable} set. This failure is deliberate: a "
                + "regeneration must never look like a passing test.");
        }

        var expected = ReadFixture();

        Assert.Equal(expected.Cases.Count, actual.Cases.Count);

        for (var index = 0; index < expected.Cases.Count; index++)
        {
            var want = expected.Cases[index];
            var got = actual.Cases[index];

            Assert.Equal(want.Name, got.Name);

            // Compared as strings, deliberately. A decimal's *scale* is part of what is being
            // pinned — 2.50 and 2.5 are the same number and not the same result — and the
            // TypeScript port has to reproduce both. Comparing numerically would let a scale
            // divergence through, which is exactly the class of bug that then moves a digit
            // three operations later.
            Assert.Equal(want.Expected.Subtotal, got.Expected.Subtotal);
            Assert.Equal(want.Expected.DiscountTotal, got.Expected.DiscountTotal);
            Assert.Equal(want.Expected.TaxTotal, got.Expected.TaxTotal);
            Assert.Equal(want.Expected.RoundingAdjustment, got.Expected.RoundingAdjustment);
            Assert.Equal(want.Expected.Total, got.Expected.Total);

            Assert.Equal(want.Expected.Lines.Count, got.Expected.Lines.Count);

            for (var line = 0; line < want.Expected.Lines.Count; line++)
            {
                Assert.Equal(want.Expected.Lines[line], got.Expected.Lines[line]);
            }
        }
    }

    [Fact]
    public void The_corpus_is_large_enough_and_covers_both_tax_modes()
    {
        // A corpus that quietly shrank — a generator edit, a filter that started refusing
        // everything — would still pass the comparison above, because both sides would agree
        // on nothing. This is the floor.
        var corpus = ReadFixture();

        Assert.True(corpus.Cases.Count > 900, $"Only {corpus.Cases.Count} carts in the corpus.");

        Assert.Contains(corpus.Cases, c => c.Cart.TaxMode == nameof(TaxMode.Inclusive));
        Assert.Contains(corpus.Cases, c => c.Cart.TaxMode == nameof(TaxMode.Exclusive));

        // And the cases a person worked on paper are still in it.
        Assert.Contains(corpus.Cases, c => c.Name == "hand-checked-inclusive");
        Assert.Contains(corpus.Cases, c => c.Name == "production-smoke-sale");
    }

    [Fact]
    public void Every_cart_in_the_corpus_exercises_something()
    {
        // Guards against a corpus that is technically large and actually trivial: five hundred
        // single-line, zero-rate, no-discount baskets would pin almost nothing.
        var corpus = ReadFixture();

        Assert.Contains(corpus.Cases, c => c.Cart.CartDiscount != "0");
        Assert.Contains(corpus.Cases, c => c.Cart.CashRoundingIncrement != "0");
        Assert.Contains(corpus.Cases, c => c.Cart.Lines.Count >= 5);
        Assert.Contains(corpus.Cases, c => c.Cart.Lines.Any(l => l.LineDiscount != "0"));
        Assert.Contains(corpus.Cases, c => c.Cart.Lines.Any(l => l.TaxRate == "0"));
        Assert.Contains(corpus.Cases, c => c.Expected.RoundingAdjustment != "0.00");
    }

    private static Corpus Generate()
    {
        var cases = new List<Case>();

        foreach (var (name, cart) in PricingCorpus.All())
        {
            var sale = PricingEngine.Price(cart);

            cases.Add(new Case(
                name,
                new CartJson(
                    [.. cart.Lines.Select(l => new CartLineJson(
                        Text(l.Quantity),
                        Text(l.UnitPrice.ToDecimal()),
                        Text(l.TaxRate),
                        Text(l.LineDiscount.ToDecimal())))],
                    Text(cart.CartDiscount.ToDecimal()),
                    cart.TaxMode.ToString(),
                    Text(cart.CashRoundingIncrement)),
                new ExpectedJson(
                    Text(sale.Subtotal.ToDecimal()),
                    Text(sale.DiscountTotal.ToDecimal()),
                    Text(sale.TaxTotal.ToDecimal()),
                    Text(sale.RoundingAdjustment.ToDecimal()),
                    Text(sale.Total.ToDecimal()),
                    [.. sale.Lines.Select(l => new PricedLineJson(
                        l.LineNumber,
                        Text(l.Gross.ToDecimal()),
                        Text(l.Subtotal.ToDecimal()),
                        Text(l.Discount.ToDecimal()),
                        Text(l.CartDiscountShare.ToDecimal()),
                        Text(l.Tax.ToDecimal()),
                        Text(l.Total.ToDecimal())))])));
        }

        return new Corpus(
            "Generated from PricingCorpus by PricingConformanceTests. Do not hand-edit.",
            PricingCorpus.Seed,
            cases);
    }

    /// <summary>
    /// A decimal as its exact literal, <b>scale included</b>.
    /// </summary>
    /// <remarks>
    /// <c>ToString()</c> on a decimal preserves trailing zeros, which is precisely what is
    /// wanted: <c>12.97</c> and <c>12.9700</c> are the same number and different results, and
    /// the port has to reproduce the scale as well as the value. Invariant culture, so a
    /// machine with a comma decimal separator generates the same file.
    /// </remarks>
    private static string Text(decimal value) =>
        value.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static Corpus ReadFixture()
    {
        Assert.True(
            File.Exists(FixturePath),
            $"The pricing corpus is missing. Run this test with {RegenerateVariable}=1 to create it.");

        return JsonSerializer.Deserialize<Corpus>(File.ReadAllText(FixturePath), Json)
            ?? throw new InvalidOperationException("The pricing corpus is empty.");
    }

    /// <summary>
    /// <c>tests/fixtures/pricing-conformance.json</c>, found by walking up from the test binary.
    /// </summary>
    /// <remarks>
    /// Walked rather than configured because both readers of this file are in different
    /// languages with different working directories, and a path relative to the repository root
    /// is the one thing they can both express.
    /// </remarks>
    private static string FixturePath
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);

            while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, ".git")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(
                directory?.FullName ?? AppContext.BaseDirectory,
                "tests",
                "fixtures",
                "pricing-conformance.json");
        }
    }

    private sealed record Corpus(string About, int Seed, IReadOnlyList<Case> Cases);

    private sealed record Case(string Name, CartJson Cart, ExpectedJson Expected);

    private sealed record CartJson(
        IReadOnlyList<CartLineJson> Lines,
        string CartDiscount,
        string TaxMode,
        string CashRoundingIncrement);

    private sealed record CartLineJson(
        string Quantity,
        string UnitPrice,
        string TaxRate,
        string LineDiscount);

    private sealed record ExpectedJson(
        string Subtotal,
        string DiscountTotal,
        string TaxTotal,
        string RoundingAdjustment,
        string Total,
        IReadOnlyList<PricedLineJson> Lines);

    private sealed record PricedLineJson(
        int LineNumber,
        string Gross,
        string Subtotal,
        string Discount,
        string CartDiscountShare,
        string Tax,
        string Total);
}
