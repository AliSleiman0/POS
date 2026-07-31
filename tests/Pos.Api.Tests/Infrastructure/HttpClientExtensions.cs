using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

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

    public static Task<HttpResponseMessage> RefreshAsync(this HttpClient client, string refreshToken)
    {
        ArgumentNullException.ThrowIfNull(client);

        return client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken });
    }
}
