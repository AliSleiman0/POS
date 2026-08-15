namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when something on an order would be fired and the menu says nowhere to send it.
/// </summary>
/// <remarks>
/// <b>The alternative is a default station, and a default station is how food gets lost.</b>
/// Routing an unconfigured item to "the first station" or "the pass" is silent, plausible and
/// wrong: the steak goes to the bar, nobody cooks it, and the first anybody knows is a customer
/// asking after forty minutes. Refusing is loud, happens at the till with a waiter standing there,
/// and names the product so a manager can fix it in the thirty seconds it takes.
/// <para>
/// <b>The whole fire is refused, not the offending line.</b> Firing half a round would put the
/// rest of the table in the kitchen with no record of what was dropped, and the waiter would have
/// no way to tell which items are cooking. Nothing is written, so the retry after the menu is
/// fixed is the same request.
/// </para>
/// <para>
/// A 409 rather than a 400: the body is well-formed and names real things, and would be accepted
/// unchanged once the product or its category has a station. There is no field to blame — what is
/// wrong is the shop's menu, and it is one the caller can change.
/// </para>
/// </remarks>
public sealed class ProductNotRoutedException : PosDomainException
{
    public ProductNotRoutedException()
        : base("Something on this order has no station to be cooked at.")
    {
    }

    public ProductNotRoutedException(string message)
        : base(message)
    {
    }

    public ProductNotRoutedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>Names the products with nowhere to go, so the message can list them.</summary>
    public static ProductNotRoutedException For(IReadOnlyList<string> productNames)
    {
        ArgumentNullException.ThrowIfNull(productNames);

        return new ProductNotRoutedException(
            $"No station is set for {string.Join(", ", productNames)}. " +
            "Set one on the product or on its category, then fire again.");
    }

    public override string ErrorType => "product-not-routed";
}
