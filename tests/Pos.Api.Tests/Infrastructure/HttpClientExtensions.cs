using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Auth;
using Pos.Api.Idempotency;

namespace Pos.Api.Tests.Infrastructure;

public sealed record TokenPair(string AccessToken, string RefreshToken);

public static class HttpClientExtensions
{
    /// <summary>Logs in through the real endpoint and returns the pair.</summary>
    public static async Task<TokenPair> LoginAsync(
        this HttpClient client,
        string tenantSlug,
        string email,
        string password)
    {
        ArgumentNullException.ThrowIfNull(client);

        var response = await client.PostAsJsonAsync(
            "/api/v1/auth/login",
            new { tenantSlug, email, password });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        return new TokenPair(
            body.GetProperty("accessToken").GetString()!,
            body.GetProperty("refreshToken").GetString()!);
    }

    public static HttpClient WithBearer(this HttpClient client, string accessToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    /// <summary>Presents an enrolled till's device token, as a register would.</summary>
    public static HttpClient WithDeviceToken(this HttpClient client, string deviceToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        client.DefaultRequestHeaders.Remove(DeviceTokenAuthenticationHandler.HeaderName);
        client.DefaultRequestHeaders.Add(DeviceTokenAuthenticationHandler.HeaderName, deviceToken);
        return client;
    }

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient client, string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
    }

    /// <summary>
    /// POSTs to one of the 🔒 routes, carrying an <c>Idempotency-Key</c>.
    /// </summary>
    /// <remarks>
    /// A <b>fresh</b> key unless one is given, because that is what an independent attempt
    /// looks like — which is what almost every test means. Passing the same key deliberately
    /// is how a test says "this is a retry", and that distinction is the whole feature.
    /// <para>
    /// The key cannot go on <c>DefaultRequestHeaders</c>: a client is reused across requests
    /// in these tests, and a shared default would make every write on it a replay of the first.
    /// </para>
    /// </remarks>
    public static Task<HttpResponseMessage> PostIdempotentAsync(
        this HttpClient client,
        string url,
        object body,
        Guid? idempotencyKey = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body),
        };

        request.Headers.Add(
            IdempotencyFilter.HeaderName,
            (idempotencyKey ?? Guid.CreateVersion7()).ToString());

        return client.SendAsync(request);
    }
}
