using Pos.Core.Entities;
using Pos.Core.Inventory;

namespace Pos.Core.Tests.Inventory;

/// <summary>
/// The rules a movement has to satisfy before anything writes it.
/// </summary>
/// <remarks>
/// The sign cases carry the weight. A receipt entered as −5, or a write-off entered as +5, is
/// one typed character away from a stock figure that is wrong by twice the quantity, in the
/// direction nobody notices until stocktake — and the database cannot tell the difference,
/// because both are perfectly good numbers for the column.
/// </remarks>
public sealed class StockRulesTests
{
    [Theory]
    [InlineData("1")]
    [InlineData("-1")]
    [InlineData("0.3500")]                    // 350g of cheese: quantities are fractional
    [InlineData("-0.0001")]
    [InlineData("999999999999999.9999")]
    [InlineData("-999999999999999.9999")]
    public void A_quantity_the_column_holds_exactly_is_storable(string value)
    {
        // Signed, unlike a price. Waste and sales are negative, and a rule that refused them
        // would be discovered by the first person writing off a broken bottle.
        Assert.True(StockRules.IsStorableQuantity(decimal.Parse(value, Culture)));
    }

    [Theory]
    [InlineData("0.00005")]                   // scale 5: Postgres rounds rather than refusing
    [InlineData("-0.00005")]
    [InlineData("1000000000000000")]
    [InlineData("-1000000000000000")]
    public void A_quantity_the_column_would_change_or_reject_is_not_storable(string value)
    {
        Assert.False(StockRules.IsStorableQuantity(decimal.Parse(value, Culture)));
    }

    [Theory]
    [InlineData(StockMovementType.Receive, "5")]
    [InlineData(StockMovementType.Refund, "1")]
    [InlineData(StockMovementType.Sale, "-1")]
    [InlineData(StockMovementType.Waste, "-3")]
    [InlineData(StockMovementType.Adjust, "2")]
    [InlineData(StockMovementType.Adjust, "-2")]
    [InlineData(StockMovementType.Recount, "0.5")]
    [InlineData(StockMovementType.Recount, "-0.5")]
    public void A_direction_that_agrees_with_the_reason_is_accepted(
        StockMovementType type,
        string quantity)
    {
        Assert.True(StockRules.IsSignConsistent(type, decimal.Parse(quantity, Culture)));
    }

    [Theory]
    [InlineData(StockMovementType.Receive, "-5")]   // a delivery that removes stock
    [InlineData(StockMovementType.Refund, "-1")]
    [InlineData(StockMovementType.Sale, "1")]       // a sale that adds stock
    [InlineData(StockMovementType.Waste, "3")]      // breakage that increases the shelf
    public void A_direction_that_contradicts_the_reason_is_refused(
        StockMovementType type,
        string quantity)
    {
        Assert.False(StockRules.IsSignConsistent(type, decimal.Parse(quantity, Culture)));
    }

    [Theory]
    [InlineData(StockMovementType.Receive)]
    [InlineData(StockMovementType.Adjust)]
    [InlineData(StockMovementType.Sale)]
    [InlineData(StockMovementType.Refund)]
    [InlineData(StockMovementType.Waste)]
    [InlineData(StockMovementType.Recount)]
    public void Nothing_moves_zero(StockMovementType type)
    {
        // Every type, because a movement that moves nothing is a row that will later be read
        // as evidence something happened. There is no reason to write one.
        Assert.False(StockRules.IsSignConsistent(type, 0m));
    }

    [Theory]
    [InlineData(StockMovementType.Receive)]
    [InlineData(StockMovementType.Adjust)]
    [InlineData(StockMovementType.Waste)]
    public void A_person_may_write_a_receipt_a_correction_or_a_write_off(StockMovementType type)
    {
        Assert.True(StockRules.IsManualAdjustment(type));
    }

    [Theory]
    [InlineData(StockMovementType.Sale)]
    [InlineData(StockMovementType.Refund)]
    [InlineData(StockMovementType.Recount)]
    public void A_person_may_not_write_a_sale_a_refund_or_a_recount(StockMovementType type)
    {
        // Sale and Refund belong to the sale that caused them and carry its id; accepting
        // them by hand would let someone fabricate sales movements with no sale behind them,
        // and the ledger would stop reconciling with the takings.
        Assert.False(StockRules.IsManualAdjustment(type));
    }

    [Fact]
    public void The_manual_types_are_listed_for_the_error_message()
    {
        // The endpoint tells a caller which types it will take, and derives that list from
        // the same predicate rather than restating it — a second copy is how the message and
        // the rule end up disagreeing.
        Assert.Equal(
            [StockMovementType.Receive, StockMovementType.Adjust, StockMovementType.Waste],
            StockRules.ManualAdjustmentTypes);
    }

    [Fact]
    public void The_movement_type_names_are_the_ones_the_check_constraint_allows()
    {
        // ck_stock_movement_type_allowed is generated from these names by HasEnumAsText.
        // Renaming a member rewrites the constraint and orphans every movement already
        // written under the old name — in an append-only table that exists to be read back.
        Assert.Equal(
            ["Receive", "Adjust", "Sale", "Refund", "Waste", "Recount"],
            Enum.GetNames<StockMovementType>());
    }

    private static System.Globalization.CultureInfo Culture =>
        System.Globalization.CultureInfo.InvariantCulture;
}
