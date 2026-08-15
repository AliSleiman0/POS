using Pos.Core.Menus;

namespace Pos.Core.Tests.Menus;

/// <summary>
/// Whether a set of chosen modifiers satisfies the questions an item asks.
/// </summary>
/// <remarks>
/// Pure, so it is exercised here exhaustively without a database — and enforced at the API,
/// because the sheet's own gating is a courtesy. A client that skipped a required group would
/// otherwise send a steak to the grill with no temperature on it.
/// </remarks>
public sealed class ModifierRulesTests
{
    private static readonly Guid Rare = Guid.CreateVersion7();
    private static readonly Guid Medium = Guid.CreateVersion7();
    private static readonly Guid WellDone = Guid.CreateVersion7();
    private static readonly Guid Chips = Guid.CreateVersion7();
    private static readonly Guid Salad = Guid.CreateVersion7();
    private static readonly Guid Unrelated = Guid.CreateVersion7();

    private static ModifierGroupRule CookedHow(int min = 1, int? max = 1) =>
        new(Guid.CreateVersion7(), "Cooked how?", min, max, new HashSet<Guid> { Rare, Medium, WellDone });

    private static ModifierGroupRule Sides(int min = 0, int? max = null) =>
        new(Guid.CreateVersion7(), "Any sides?", min, max, new HashSet<Guid> { Chips, Salad });

    [Fact]
    public void An_item_with_no_questions_accepts_no_choices()
    {
        Assert.Empty(ModifierRules.Check([], []));
    }

    [Fact]
    public void A_required_group_with_nothing_chosen_is_refused_by_name()
    {
        var violations = ModifierRules.Check([CookedHow()], []);

        var violation = Assert.Single(violations);

        // The question, in the message. "Choose an option" on its own tells a waiter nothing
        // when the item asks three of them.
        Assert.Equal("Cooked how?", violation.GroupName);
        Assert.Contains("Cooked how?", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_required_group_answered_once_is_satisfied()
    {
        Assert.Empty(ModifierRules.Check([CookedHow()], [Medium]));
    }

    [Fact]
    public void More_than_the_maximum_is_refused()
    {
        var violations = ModifierRules.Check([CookedHow()], [Rare, WellDone]);

        var violation = Assert.Single(violations);
        Assert.Contains("Only one option", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_optional_group_left_empty_is_fine()
    {
        Assert.Empty(ModifierRules.Check([Sides()], []));
    }

    [Fact]
    public void An_unlimited_group_takes_as_many_as_it_is_given()
    {
        // "As many toppings as you like" is a real menu, which is why the maximum is nullable
        // rather than a large sentinel somebody would eventually order past.
        Assert.Empty(ModifierRules.Check([Sides()], [Chips, Salad, Chips, Salad]));
    }

    [Fact]
    public void The_same_option_twice_counts_twice()
    {
        // "Two extra shots" is two selections of one option, not two kinds of thing — and a
        // shop that allows a maximum of two means two shots. Distinct-counting here would
        // silently let a customer order four and be charged for four.
        var violations = ModifierRules.Check([Sides(min: 0, max: 2)], [Chips, Chips, Chips]);

        var violation = Assert.Single(violations);
        Assert.Contains("At most 2", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_option_from_a_group_the_item_does_not_ask_is_refused_not_ignored()
    {
        /*
         * The case worth being deliberate about.
         *
         * Dropping it silently would either charge for something the kitchen never heard about,
         * or cook something nobody is paying for — and which of those it was would depend on
         * where in the pipeline the drop happened, which is the worst kind of bug to chase.
         */
        var violations = ModifierRules.Check([CookedHow()], [Medium, Unrelated]);

        var violation = Assert.Single(violations);
        Assert.Contains("not offered", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Several_broken_rules_are_all_reported_at_once()
    {
        // One round trip, one list. A waiter who has to fix three things should be told three
        // things, not discover them one refusal at a time with a customer waiting.
        var violations = ModifierRules.Check(
            [CookedHow(), Sides(min: 1, max: 1)],
            [Rare, WellDone, Chips, Salad]);

        Assert.Equal(2, violations.Count);
        Assert.Contains(violations, v => v.GroupName == "Cooked how?");
        Assert.Contains(violations, v => v.GroupName == "Any sides?");
    }

    [Fact]
    public void A_minimum_above_one_says_how_many()
    {
        var violations = ModifierRules.Check([Sides(min: 2)], [Chips]);

        var violation = Assert.Single(violations);
        Assert.Contains("at least 2", violation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_below_its_minimum_is_reported_once_rather_than_twice()
    {
        // Below the minimum and above nothing: the maximum check is skipped, so a single
        // question produces a single message rather than a contradictory pair.
        var violations = ModifierRules.Check([Sides(min: 2, max: 3)], []);

        Assert.Single(violations);
    }
}
