using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Tenders;

namespace Pos.Core.Tests.Tenders;

/// <summary>
/// What a tip does to the change, and therefore to the drawer.
/// </summary>
/// <remarks>
/// The product is cash-only, so a tip is physically cash left behind. The whole mechanism is
/// that it comes out of the over-tender rather than being added to the total: expected cash sums
/// tendered less change given, so a smaller change figure leaves the tip in the drawer without
/// <c>ShiftArithmetic</c> changing at all.
/// </remarks>
public sealed class TipTenderRulesTests
{
    [Fact]
    public void A_tip_reduces_the_change_rather_than_increasing_the_total()
    {
        // €25 against a €20 bill with a €5 tip is nothing back — not €5 of change.
        var change = TenderRules.ChangeFor((Money)20m, [(Money)25m], (Money)5m);

        Assert.Equal((Money)0m, change);
    }

    [Fact]
    public void The_excess_over_the_total_and_the_tip_still_comes_back()
    {
        // €30 against a €20 bill with a €5 tip: €5 back. Over-tender is ordinary and a tip does
        // not turn the remainder into more tip.
        var change = TenderRules.ChangeFor((Money)20m, [(Money)30m], (Money)5m);

        Assert.Equal((Money)5m, change);
    }

    [Fact]
    public void A_tender_that_covers_the_bill_but_not_the_tip_is_refused()
    {
        /*
         * The failure worth having.
         *
         * €22 against a €20 bill with a €5 tip is not "€2 of tip and we will call it evens" —
         * it is somebody having keyed the tip wrong, and silently reducing it would record a
         * gratuity the customer did not leave and a drawer that balanced against a lie.
         */
        var refused = Assert.Throws<UnderTenderException>(
            () => TenderRules.ChangeFor((Money)20m, [(Money)22m], (Money)5m));

        // Named separately from an ordinary under-tender, because the two are different
        // mistakes: one needs another note from the customer, the other is a keying slip
        // somebody can correct on their own.
        Assert.Contains("tip", refused.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_tip_behaves_exactly_as_it_did_before()
    {
        // The default, and every counter sale. The parameter is optional so the retail path is
        // unchanged rather than merely equivalent.
        Assert.Equal((Money)1.55m, TenderRules.ChangeFor((Money)18.45m, [(Money)20m]));
        Assert.Equal((Money)1.55m, TenderRules.ChangeFor((Money)18.45m, [(Money)20m], Money.Zero));
    }

    [Fact]
    public void A_split_tender_covers_the_tip_between_them()
    {
        // Four people paying a quarter each of a €40 bill with a €10 tip. This is the "even
        // split" case in full: N tenders against one sale, and no bill-splitting mechanism.
        var change = TenderRules.ChangeFor(
            (Money)40m,
            [(Money)12.50m, (Money)12.50m, (Money)12.50m, (Money)12.50m],
            (Money)10m);

        Assert.Equal((Money)0m, change);
    }

    [Theory]
    [InlineData("0.01")]
    [InlineData("0.0001")]
    public void A_tip_at_the_smallest_storable_scale_is_carried_exactly(string amount)
    {
        // numeric(19,4). A tip is money and takes the same scale as everything else, so it
        // cannot quietly round into or out of the drawer.
        var tip = (Money)decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);

        var change = TenderRules.ChangeFor((Money)10m, [(Money)10m + tip], tip);

        Assert.Equal(Money.Zero, change);
    }
}
