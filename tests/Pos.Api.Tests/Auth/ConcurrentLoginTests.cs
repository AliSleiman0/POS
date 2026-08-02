using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Auth;

/// <summary>
/// Several sessions starting for the same account at the same instant.
/// </summary>
/// <remarks>
/// Not a hypothetical: a shop where two tills share the owner's login, a manager signed in on
/// the counter tablet and the back-office machine, or one person double-clicking Sign in.
/// <para>
/// This was a real 500. <c>LoginAsync</c> stamped <c>LastLoginAt</c> through
/// <c>UserManager.UpdateAsync</c>, which checks Identity's <c>ConcurrencyStamp</c> and — this
/// is the part that made it invisible — <b>returns a failed result rather than throwing</b>.
/// Nothing checked the result, the user entity stayed <c>Modified</c> in the tracker, and the
/// next <c>SaveChangesAsync</c> (inserting the refresh token) retried it and threw. The
/// password had already been verified, so the caller was refused after succeeding.
/// <para>
/// Found by the Phase 4 Playwright suite running its workers in parallel. No sequential test
/// could have caught it, which is CLAUDE.md invariant 9's "concurrency tests must actually run
/// concurrently" — so these fire genuinely simultaneous requests rather than a loop.
/// </para>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class ConcurrentLoginTests(PosApiFactory factory)
{
    private const string Password = "Correct-Horse-9";

    private const int Attempts = 8;

    [Fact]
    public async Task Simultaneous_logins_for_one_account_all_succeed()
    {
        var tenant = await factory.CreateTenantAsync("login-race");
        await factory.CreateUserAsync(
            tenant.Id, "owner@example.com", Password, RoleNames.Owner, "Ann Owner");

        using var client = factory.CreateClient();

        // A gate, so the requests are actually in flight together. Awaiting them in a loop
        // would let each finish before the next began, and would pass against the bug.
        using var gate = new SemaphoreSlim(0, Attempts);

        var logins = Enumerable.Range(0, Attempts).Select(async _ =>
        {
            await gate.WaitAsync();

            return await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new { tenantSlug = "login-race", email = "owner@example.com", password = Password });
        }).ToArray();

        gate.Release(Attempts);

        var responses = await Task.WhenAll(logins);

        try
        {
            var statuses = responses.Select(r => r.StatusCode).ToArray();

            Assert.All(statuses, status => Assert.Equal(HttpStatusCode.OK, status));
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Each_simultaneous_login_gets_its_own_refresh_token()
    {
        var tenant = await factory.CreateTenantAsync("login-race-tokens");
        await factory.CreateUserAsync(
            tenant.Id, "owner@example.com", Password, RoleNames.Owner, "Ann Owner");

        using var client = factory.CreateClient();
        using var gate = new SemaphoreSlim(0, Attempts);

        var logins = Enumerable.Range(0, Attempts).Select(async _ =>
        {
            await gate.WaitAsync();

            using var response = await client.PostAsJsonAsync(
                "/api/v1/auth/login",
                new
                {
                    tenantSlug = "login-race-tokens",
                    email = "owner@example.com",
                    password = Password,
                });

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<JsonElement>();

            return body.GetProperty("refreshToken").GetString();
        }).ToArray();

        gate.Release(Attempts);

        var tokens = await Task.WhenAll(logins);

        // Distinct families, because these are distinct sessions. Two tills sharing a token
        // would mean either one rotating it out from under the other — and reuse of a rotated
        // token revokes the whole family, so the shop would log itself out at random.
        Assert.Equal(Attempts, tokens.Distinct(StringComparer.Ordinal).Count());
    }
}
