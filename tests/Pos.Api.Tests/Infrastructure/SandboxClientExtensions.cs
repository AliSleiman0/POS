using Pos.Data.Identity;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>Signing into the shared catalog sandbox as one of its three roles.</summary>
public static class SandboxClientExtensions
{
    /// <summary>
    /// A client holding an access token for the sandbox tenant, and the sandbox itself.
    /// </summary>
    /// <remarks>
    /// Obtained by logging in, not by minting a token: the point of these tests is what a
    /// real caller can reach, and a hand-made token would be answering a different question.
    /// Token forging is the subject of its own tests, in <c>TestTokens</c>.
    /// </remarks>
    public static async Task<(HttpClient Client, CatalogSandbox Sandbox)> SignedInAsync(
        this PosApiFactory factory,
        string role)
    {
        ArgumentNullException.ThrowIfNull(factory);

        var sandbox = await factory.CatalogSandboxAsync();

        var email = role switch
        {
            RoleNames.Owner => CatalogSandbox.OwnerEmail,
            RoleNames.Manager => CatalogSandbox.ManagerEmail,
            RoleNames.Cashier => CatalogSandbox.CashierEmail,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "No sandbox user holds that role."),
        };

        var client = factory.CreateClient();

        client.WithBearer(
            (await client.LoginAsync(CatalogSandbox.Slug, email, CatalogSandbox.Password)).AccessToken);

        return (client, sandbox);
    }
}
