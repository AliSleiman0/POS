using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Monetary;
using Pos.Core.Tenders;

namespace Pos.Core.Tests.Tenders;

/// <summary>
/// Change due, split payments, and the tender the register must refuse.
/// </summary>
public sealed class TenderRulesTests
{
    [Fact]
    public void Two_tenders_settle_one_sale()
    {
        // Split payment is ordinary retail, and it is the reason Tender is a collection of
        // rows rather than an amount column on Sale.
        Assert.Equal(
            (Money)0m,
            TenderRules.ChangeFor((Money)18.45m, [(Money)10m, (Money)8.45m]));
    }

    [Fact]
    public void An_exact_tender_gives_no_change()
    {
        Assert.True(TenderRules.ChangeFor((Money)18.45m, [(Money)18.45m]).IsZero);
    }

    [Fact]
    public void An_over_tender_gives_the_difference_as_change()
    {
        // The everyday case: a 20 note against 18.45.
        Assert.Equal((Money)1.55m, TenderRules.ChangeFor((Money)18.45m, [(Money)20m]));
    }

    [Fact]
    public void An_under_tender_is_refused()
    {
        var exception = Assert.Throws<UnderTenderException>(
            () => TenderRules.ChangeFor((Money)18.45m, [(Money)10m]));

        // Refused rather than recorded as a part payment: a half-paid sale is not a state
        // this system has, and an unpaid balance is a debt the POS cannot collect or report.
        Assert.Equal("under-tender", exception.ErrorType);
        Assert.False(TenderRules.IsSufficient((Money)18.45m, [(Money)10m]));
    }

    [Fact]
    public void A_sale_with_no_tenders_at_all_is_an_under_tender()
    {
        // An empty list sums to zero, which covers a zero total and nothing else. A fully
        // comped sale therefore settles with no tenders, which is correct and is why this is
        // not simply "at least one tender required".
        Assert.Throws<UnderTenderException>(
            () => TenderRules.ChangeFor((Money)1m, []));

        Assert.True(TenderRules.ChangeFor(Money.Zero, []).IsZero);
    }

    [Fact]
    public void Sufficiency_answers_without_throwing()
    {
        // Endpoints in this codebase report every bad field at once rather than the first, so
        // the validation path needs a predicate it can call alongside the others.
        Assert.True(TenderRules.IsSufficient((Money)10m, [(Money)10m]));
        Assert.False(TenderRules.IsSufficient((Money)10m, [(Money)9.99m]));
    }

    [Fact]
    public void A_refunds_tenders_are_negative_and_never_give_change()
    {
        // A refund's tenders match its negative total, so sum(Tender) == Total on a refund
        // and >= on a sale is the whole of invariant 2 with no special case. A positive
        // tender on a refund would make the shift's expected cash rise when money left the
        // drawer — wrong by twice the refund, in the direction that looks like theft.
        Assert.True(TenderRules.IsSignConsistent(SaleType.Refund, (Money)(-18.45m)));
        Assert.False(TenderRules.IsSignConsistent(SaleType.Refund, (Money)18.45m));

        Assert.True(TenderRules.IsSignConsistent(SaleType.Sale, (Money)18.45m));
        Assert.False(TenderRules.IsSignConsistent(SaleType.Sale, (Money)(-18.45m)));
    }

    [Fact]
    public void A_zero_tender_is_consistent_with_either_direction()
    {
        // Neither adds nor removes cash, so neither direction is contradicted. Stated so the
        // boundary is a decision rather than whichever way the comparison happened to fall.
        Assert.True(TenderRules.IsSignConsistent(SaleType.Sale, Money.Zero));
        Assert.True(TenderRules.IsSignConsistent(SaleType.Refund, Money.Zero));
    }

    [Fact]
    public void Only_cash_is_accepted_today()
    {
        Assert.True(TenderRules.IsAccepted(TenderMethod.Cash));

        // Declared but not implemented. Without this check a client could post a Card tender
        // that no processor ever saw, and it would sit in the takings reconciling against
        // nothing.
        Assert.False(TenderRules.IsAccepted(TenderMethod.Card));
        Assert.False(TenderRules.IsAccepted(TenderMethod.External));
        Assert.False(TenderRules.IsAccepted(TenderMethod.Voucher));

        Assert.Equal([TenderMethod.Cash], TenderRules.AcceptedMethods);
    }

    [Fact]
    public void Adding_a_method_needs_no_sale_schema_change()
    {
        // The phase doc asks for this conclusion to be reasoned through and recorded. Making
        // it a test rather than a comment means the reasoning is checkable:
        //
        //   1. Sale declares no tender-shaped property at all — no Method, no AmountTendered,
        //      no ChangeGiven. Payment lives entirely in the Tender rows.
        //   2. TenderMethod already carries values the MVP does not implement, and the column
        //      is text with a check constraint generated from the enum's names.
        //
        // So a new method is a new enum member and a widened check constraint: one migration
        // on `tender`, and none on `sale`. If someone later hangs a payment column on Sale to
        // save a join, this test is what says no.
        var saleProperties = typeof(Sale).GetProperties().Select(p => p.Name).ToArray();

        Assert.DoesNotContain("Method", saleProperties, StringComparer.Ordinal);
        Assert.DoesNotContain("ChangeGiven", saleProperties, StringComparer.Ordinal);
        Assert.DoesNotContain("AmountTendered", saleProperties, StringComparer.Ordinal);

        Assert.True(
            Enum.GetValues<TenderMethod>().Length > TenderRules.AcceptedMethods.Count,
            "The discriminator claim is only meaningful if the enum can already hold a method "
            + "the MVP does not take.");
    }
}
