using Pos.Core.Menus;

namespace Pos.Core.Tests.Menus;

/// <summary>
/// Where an item is cooked, resolved from what the menu says.
/// </summary>
/// <remarks>
/// Pure, so every menu shape is exercised here without a database. The case that matters most is
/// the last one: nothing routed is <b>null</b>, never a default station, because a default station
/// sends a steak to the bar silently and the first anybody knows is a customer asking after forty
/// minutes.
/// </remarks>
public sealed class StationRoutingTests
{
    private static readonly Guid Grill = Guid.CreateVersion7();
    private static readonly Guid Bar = Guid.CreateVersion7();
    private static readonly Guid Pass = Guid.CreateVersion7();

    [Fact]
    public void A_station_on_the_product_wins()
    {
        Assert.Equal(Pass, StationRouting.Resolve(Pass, [Bar, Grill]));
    }

    [Fact]
    public void A_product_with_no_station_takes_its_categorys()
    {
        Assert.Equal(Bar, StationRouting.Resolve(null, [Bar]));
    }

    [Fact]
    public void The_chain_is_walked_until_something_is_set()
    {
        // "Wine" and "Drinks" set nothing; the shop configured routing once, at the top.
        Assert.Equal(Bar, StationRouting.Resolve(null, [null, null, Bar]));
    }

    [Fact]
    public void The_nearest_category_wins_over_its_parent()
    {
        // The whole point of walking rather than taking the root: "Drinks → Bar" covers the
        // list, and the one bottled cocktail finished at the pass overrides it one level down.
        Assert.Equal(Pass, StationRouting.Resolve(null, [Pass, Bar]));
    }

    [Fact]
    public void Nothing_set_anywhere_is_unrouted_rather_than_a_default()
    {
        Assert.Null(StationRouting.Resolve(null, [null, null]));
    }

    [Fact]
    public void An_uncategorised_product_with_no_station_is_unrouted()
    {
        Assert.Null(StationRouting.Resolve(null, []));
    }

    [Fact]
    public void An_uncategorised_product_can_still_be_routed_on_itself()
    {
        Assert.Equal(Grill, StationRouting.Resolve(Grill, []));
    }
}
