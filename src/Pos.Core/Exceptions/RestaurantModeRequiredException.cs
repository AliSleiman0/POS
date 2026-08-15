namespace Pos.Core.Exceptions;

/// <summary>
/// Thrown when an order endpoint is called by a shop that is running as a retail counter.
/// </summary>
/// <remarks>
/// <b>A 409 rather than a 403 or a 404, and the distinction is worth being deliberate about.</b>
/// The caller is not forbidden — an owner holds every policy there is — and the route genuinely
/// exists, so a 404 would send a client hunting for a typo. What is wrong is the shop's state,
/// and it is a state the caller can change: turn restaurant mode on at <c>/admin/settings</c> and
/// the same request succeeds. That is exactly what a 409 means.
/// <para>
/// It also keeps the routing table static, which matters more than it looks: the authorization
/// matrix and the isolation manifest are both derived from the routes the application maps, and
/// routes that appeared and disappeared with a tenant setting would be covered for some shops and
/// not others.
/// </para>
/// </remarks>
public sealed class RestaurantModeRequiredException : PosDomainException
{
    public RestaurantModeRequiredException()
        : base("This shop is set up as a retail counter. Turn on restaurant mode in the settings to take table orders.")
    {
    }

    public RestaurantModeRequiredException(string message)
        : base(message)
    {
    }

    public RestaurantModeRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public override string ErrorType => "restaurant-mode-required";
}
