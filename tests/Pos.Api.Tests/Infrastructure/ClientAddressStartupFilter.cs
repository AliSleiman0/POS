using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>
/// Decides what client address the hosted application sees.
/// </summary>
/// <remarks>
/// Every request through <c>WebApplicationFactory</c> arrives with no remote address at
/// all, so without this the entire assembly is one rate-limit partition. Hundreds of tests
/// call <c>LoginAsync</c>, the login limiter caps attempts per address, and the suite
/// exhausts its own budget within seconds — six hundred failures that say nothing about the
/// code.
/// <para>
/// <b>The collision is not an artefact of testing.</b> It is exactly what a shop behind NAT
/// has: one address for the whole staff and every till. That is why
/// <see cref="RateLimitingServiceCollectionExtensions.LoginAttemptsPerWindow"/> is set for a
/// shop rather than for a person, and it is worth knowing that the suite found that out
/// before a customer did.
/// </para>
/// <para>
/// So: <see cref="PosApiFactory"/> gives every request a fresh address, which is the
/// "different people on different connections" case and keeps ordinary tests deterministic.
/// A test that is actually about the limiter pins one address instead, and then the limiter
/// behaves exactly as it would for one attacker.
/// </para>
/// <para>
/// Prepended through an <see cref="IStartupFilter"/> so it runs before the rate limiter and
/// before anything else reads the address, without a test having to rebuild the
/// application's pipeline.
/// </para>
/// </remarks>
public sealed class ClientAddressStartupFilter(Func<IPAddress> address) : IStartupFilter
{
    /// <summary>A fresh address, from the range reserved for documentation.</summary>
    /// <remarks>
    /// TEST-NET-3 (203.0.113.0/24) is reserved for examples, so a value from it cannot
    /// collide with anything real if one ever escapes into a log. 254 hosts is enough that
    /// two concurrent requests colliding is harmless — the limit is thirty per address per
    /// minute, and nothing in the suite comes close to that per address.
    /// </remarks>
    public static IPAddress Unique() =>
        new(new byte[] { 203, 0, 113, (byte)Random.Shared.Next(1, 255) });

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        builder =>
        {
            builder.Use(async (context, proceed) =>
            {
                context.Connection.RemoteIpAddress = address();
                await proceed(context);
            });

            next(builder);
        };

    /// <summary>Registers this filter on a test host.</summary>
    public static void Register(IServiceCollection services, Func<IPAddress> address) =>
        services.AddSingleton<IStartupFilter>(new ClientAddressStartupFilter(address));

    /// <summary>Registers it with one fixed address, for a test about the limiter itself.</summary>
    public static void Register(IServiceCollection services, IPAddress address) =>
        Register(services, () => address);
}
