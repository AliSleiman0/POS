using Microsoft.EntityFrameworkCore;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Data;

namespace Pos.Api.Orders;

/// <summary>
/// Refuses an order or kitchen request from a shop that is running as a retail counter.
/// </summary>
/// <remarks>
/// <b>A filter rather than a check in each handler, so coverage is not a matter of remembering.</b>
/// The group carries it once and every route added to that group inherits it — including the ones
/// somebody adds next year without reading this file.
/// <para>
/// It costs one indexed read of a single row per request, and that read is already warm: the
/// tenant row is touched by receipts, by settings and by every quote. The alternative — putting
/// the mode in the access token — would be stale for up to fifteen minutes after an owner changed
/// it, which is exactly long enough for somebody to turn restaurant mode on and conclude it does
/// not work.
/// </para>
/// </remarks>
internal sealed class RestaurantModeFilter(AppDbContext db) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var cancellationToken = context.HttpContext.RequestAborted;

        // Filtered by hand, because Tenant carries no query filter — it is the list of tenants
        // and login has to resolve a row in it before any tenant is known. Same shape as the
        // settings and receipt paths.
        var mode = await db.Tenants
            .AsNoTracking()
            .Where(t => t.Id == db.CurrentTenantId)
            .Select(t => t.ServiceMode)
            .FirstOrDefaultAsync(cancellationToken);

        if (mode != ServiceMode.Restaurant)
        {
            throw new RestaurantModeRequiredException();
        }

        return await next(context);
    }
}

/// <summary>Wiring for the routes that only exist for a restaurant.</summary>
public static class RestaurantModeEndpointExtensions
{
    /// <summary>
    /// Answers <c>409 restaurant-mode-required</c> unless the tenant is in restaurant mode.
    /// </summary>
    /// <remarks>
    /// <b>The route is always mapped</b>, and that is deliberate rather than incidental. The
    /// authorization matrix and the isolation manifest are both derived from the routes the
    /// application maps, so routes that appeared and disappeared with a tenant setting would be
    /// covered for some shops and not for others — and the gap would look exactly like a passing
    /// test suite.
    /// </remarks>
    public static RouteGroupBuilder RequireRestaurantMode(this RouteGroupBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter<RestaurantModeFilter>();

        return builder;
    }
}
