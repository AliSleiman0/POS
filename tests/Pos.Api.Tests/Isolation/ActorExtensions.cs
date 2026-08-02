using System.Net.Http.Json;
using Pos.Api.Tests.Infrastructure;

namespace Pos.Api.Tests.Isolation;

public static class ActorExtensions
{
    /// <summary>
    /// A client carrying whatever credentials <paramref name="actor"/> holds, obtained the
    /// way a real caller would obtain them.
    /// </summary>
    /// <remarks>
    /// Every actor here belongs to tenant B. The suite never fabricates a session for
    /// tenant A — the whole question being asked is what a legitimate caller in one tenant
    /// can reach in another, and a hand-made token would be answering a different one.
    /// Tokens are minted directly in exactly one place, <see cref="TestTokens"/>, where
    /// forging them is the subject rather than a shortcut.
    /// </remarks>
    public static async Task<HttpClient> ClientForAsync(
        this PosApiFactory factory,
        Actor actor,
        TwoTenantWorld world)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(world);

        var client = factory.CreateClient();

        switch (actor)
        {
            case Actor.Anonymous:
                break;

            case Actor.OwnerOfB:
                client.WithBearer(
                    (await client.LoginAsync(world.B.Slug, TwoTenantWorld.OwnerEmail, TwoTenantWorld.Password))
                    .AccessToken);
                break;

            case Actor.CashierOfB:
                client.WithBearer(
                    (await client.LoginAsync(world.B.Slug, TwoTenantWorld.CashierEmail, TwoTenantWorld.Password))
                    .AccessToken);
                break;

            case Actor.DeviceOfB:
                client.WithDeviceToken(world.B.FrontCounter.DeviceToken);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(actor), actor, "Unhandled actor.");
        }

        return client;
    }

    /// <summary>Issues the request an <see cref="IsolationCase"/> describes.</summary>
    public static Task<HttpResponseMessage> SendAsync(
        this HttpClient client,
        IsolationCase testCase,
        TwoTenantWorld world,
        string url)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(testCase);

        return testCase.Method switch
        {
            "GET" => client.GetAsync(new Uri(url, UriKind.Relative)),

            // A fresh key on every 🔒 route, so the probe reaches the handler instead of
            // being turned away with a 400 on the header — which would make a by-id theory
            // expecting 404 pass for the wrong reason.
            "POST" when testCase.Idempotent =>
                client.PostIdempotentAsync(url, testCase.Body?.Invoke(world) ?? new { }),

            "POST" => client.PostAsJsonAsync(url, testCase.Body?.Invoke(world) ?? new { }),
            "PUT" => client.PutAsJsonAsync(url, testCase.Body?.Invoke(world) ?? new { }),

            // No body, deliberately: a DELETE that carried one would be describing a request
            // the endpoints do not accept, and none of them reads one.
            "DELETE" => client.DeleteAsync(new Uri(url, UriKind.Relative)),
            _ => throw new NotSupportedException(
                $"The isolation theories do not know how to send '{testCase.Method}' yet. "
                + "Add it here when an endpoint needs it."),
        };
    }
}
