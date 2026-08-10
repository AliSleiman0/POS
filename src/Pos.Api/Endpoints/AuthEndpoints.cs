using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Pos.Api.Auth;
using Pos.Api.Observability;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Endpoints;

public sealed record LoginRequest(string TenantSlug, string Email, string Password);

public sealed record PinLoginRequest(Guid UserId, string Pin);

/// <summary>A manager authorising one privileged action on somebody else's session.</summary>
/// <remarks>
/// <c>Policies</c> is the set the cart needs <i>now</i>, not one name: a cart carrying both a
/// discount and a price override would otherwise need two grants, two headers and two PIN
/// entries for an authorisation the manager gave once.
/// </remarks>
public sealed record OverrideRequest(Guid UserId, string Pin, IReadOnlyList<string> Policies);

/// <summary>
/// A minted grant. <c>Grant</c> is shown once and stored nowhere on the server but as a digest.
/// </summary>
public sealed record OverrideGrantResponse(
    string Grant,
    int ExpiresIn,
    Guid AuthorizedById,
    string AuthorizedByName,
    IReadOnlyList<string> Policies);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record AuthUser(Guid Id, string DisplayName, string? Role, IReadOnlyList<string> Policies);

public sealed record AuthResponse(string AccessToken, string RefreshToken, int ExpiresIn, AuthUser User);

public sealed record TenantSettings(string Slug, string Name, string CurrencyCode, string TimeZoneId, string TaxMode);

public sealed record MeResponse(AuthUser User, TenantSettings Tenant);

/// <remarks>
/// Every handler below returns a <c>Results&lt;…&gt;</c> union rather than a bare
/// <c>IResult</c>, and that is a contract requirement rather than a style preference.
/// OpenAPI infers a response schema from the declared return type; a handler typed
/// <c>Task&lt;IResult&gt;</c> produces an operation with <b>no response content at all</b>, and
/// Phase 4.1's generated TypeScript client then types the login and <c>/me</c> bodies as
/// <c>never</c> — so the one screen every user meets first would have had to be written against
/// hand-written DTOs, which CLAUDE.md forbids precisely because they drift.
/// <para>
/// <c>ResponseSchemaContractTests.Every_endpoint_that_returns_a_body_declares_its_schema</c> fails the build if a
/// handler here reverts to <c>IResult</c>.
/// </para>
/// </remarks>
public static class AuthEndpoints
{
    /// <summary>
    /// A hash of a value nobody knows, verified against on every miss so that "no such
    /// tenant", "no such user" and "wrong password" cost about the same. Skipping the work
    /// on the miss path turns response time into an oracle that answers "does this account
    /// exist?" — which is how a credential-stuffing list gets refined into a target list.
    /// </summary>
    private static readonly string DecoyPasswordHash =
        new PasswordHasher<ApplicationUser>().HashPassword(new ApplicationUser(), Guid.NewGuid().ToString());

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var auth = builder.MapGroup("/api/v1/auth").WithTags("Auth");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithSummary("Exchange tenant slug, email and password for a token pair");

        auth.MapPost("/pin", PinLoginAsync)
            .RequireAuthorization(DeviceTokenAuthenticationHandler.PolicyName)
            .RequireRateLimiting(RateLimitPolicies.PinAttempts)
            .WithSummary("Start a cashier session from an enrolled till");

        auth.MapPost("/override", OverrideAsync)
            .RequireAuthorization(DeviceTokenAuthenticationHandler.PolicyName)
            .RequireRateLimiting(RateLimitPolicies.PinAttempts)
            .WithSummary("Manager PIN authorising one privileged action, without swapping the session");

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithSummary("Rotate a refresh token");

        auth.MapPost("/logout", LogoutAsync)
            .RequireAuthorization()
            .WithSummary("Revoke the current refresh-token family");

        auth.MapGet("/me", MeAsync)
            .RequireAuthorization()
            .WithSummary("Current user, granted policies and tenant settings");

        return builder;
    }

    private static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request,
        AppDbContext db,
        AmbientTenantContext tenantContext,
        UserManager<ApplicationUser> users,
        TokenService tokens,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var slug = Tenant.NormalizeSlug(request.TenantSlug);

        // The tenant table is the one table with no tenant filter — it is the list of
        // tenants, and this lookup is what lets every table after it be scoped.
        var tenant = slug is null
            ? null
            : await db.Tenants.FirstOrDefaultAsync(t => t.Slug == slug && t.IsActive, cancellationToken);

        if (tenant is null)
        {
            return RejectCredentials(users, request.Password);
        }

        tenantContext.Resolve(tenant.Id);

        // Tenant-filtered, so this cannot find a user at another shop even if the email
        // matches one exactly — which it will, because people use the same email everywhere.
        var user = await users.FindByEmailAsync(request.Email);

        if (user is null || !user.IsActive)
        {
            return RejectCredentials(users, request.Password);
        }

        if (await users.IsLockedOutAsync(user))
        {
            return RejectCredentials(users, request.Password);
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            return RejectCredentials(users, password: null);
        }

        await users.ResetAccessFailedCountAsync(user);

        await RecordLoginAsync(db, user, timeProvider.GetUtcNow(), cancellationToken);

        return TypedResults.Ok(await BuildAuthResponseAsync(tokens, user, registerId: null, cancellationToken));
    }

    private static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> PinLoginAsync(
        PinLoginRequest request,
        HttpContext http,
        AppDbContext db,
        UserManager<ApplicationUser> users,
        TokenService tokens,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        if (!TryReadRegister(http, out var registerId))
        {
            return InvalidCredentials();
        }

        var (user, failure) = await CheckPinAsync(users, request.UserId, request.Pin, cancellationToken);

        if (user is null)
        {
            return failure!;
        }

        var now = timeProvider.GetUtcNow();

        await RecordLoginAsync(db, user, now, cancellationToken);

        // Answers "is that lost tablet still being used?" without a write on every request.
        // Set-based for the same reason as the line above: two cashiers swapping in on one
        // till at the same moment must not collide.
        await db.Registers
            .Where(r => r.Id == registerId)
            .ExecuteUpdateAsync(r => r.SetProperty(x => x.LastSeenAt, now), cancellationToken);

        return TypedResults.Ok(await BuildAuthResponseAsync(tokens, user, registerId, cancellationToken));
    }

    /// <summary>
    /// A manager's PIN, exchanged for authorisation to do one thing on someone else's session.
    /// </summary>
    /// <remarks>
    /// The cashier's session is not touched. That is the whole point: <c>POST /sales</c> takes
    /// the cashier from the token, so a flow that swapped the session would attribute the sale —
    /// and the override — to the manager, and the drawer's Z-report would reconcile against the
    /// wrong person. Here the sale stays the cashier's and <c>SaleLine.OverriddenBy</c> records
    /// who authorised the exception.
    /// <para>
    /// Device-token authenticated, exactly like <c>/auth/pin</c>: a PIN is never sufficient on
    /// its own, and a grant minted from a laptop somewhere would defeat the point of standing at
    /// the till to give it.
    /// </para>
    /// </remarks>
    private static async Task<Results<Ok<OverrideGrantResponse>, ProblemHttpResult, ValidationProblem>> OverrideAsync(
        OverrideRequest request,
        HttpContext http,
        UserManager<ApplicationUser> users,
        TokenService tokens,
        OverrideGrantService grants,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        if (!TryReadRegister(http, out var registerId))
        {
            return InvalidCredentials();
        }

        var requested = (request.Policies ?? []).Distinct(StringComparer.Ordinal).ToArray();

        if (requested.Length == 0 || !OverrideGrantService.AreGrantable(requested))
        {
            // 400 rather than 403, and checked before the PIN: asking for CanManageEmployees is
            // a malformed request, not a permission that might be granted on a better day. The
            // allow-list is what stops this endpoint being a general elevation mechanism.
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["policies"] =
                    [$"One or more of {string.Join(", ", OverrideGrantService.Grantable)} is required."],
            });
        }

        var (user, failure) = await CheckPinAsync(users, request.UserId, request.Pin, cancellationToken);

        if (user is null)
        {
            return failure!;
        }

        var role = await tokens.ResolveRoleAsync(user);
        var held = PolicyCatalog.PoliciesFor(role);

        if (!requested.All(policy => held.Contains(policy, StringComparer.Ordinal)))
        {
            // Says plainly that this person cannot authorise it, which does disclose that they
            // are not a manager — but only to somebody who already knows their PIN, so it is
            // not an oracle anyone at the counter can query. The alternative, answering
            // "invalid credentials", tells a manager their own correct PIN is wrong.
            return NotPermittedToAuthorize();
        }

        var token = await grants.IssueAsync(
            user.TenantId,
            user.Id,
            registerId,
            requested,
            cancellationToken);

        return TypedResults.Ok(new OverrideGrantResponse(
            token,
            (int)OverrideGrant.Lifetime.TotalSeconds,
            user.Id,
            user.DisplayName,
            requested));
    }

    private static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshRequest request,
        TokenService tokens,
        PosMetrics metrics,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(metrics);

        var result = await tokens.RotateAsync(request.RefreshToken, cancellationToken);

        if (!result.Succeeded)
        {
            // Worth counting rather than merely logging. One rejection is a tab left open
            // overnight; a spike is either every till being signed out mid-shift or a
            // token family being replayed — and those need opposite responses. The
            // response to the caller is deliberately the same either way.
            metrics.RefreshRejected();

            return InvalidCredentials();
        }

        var role = await tokens.ResolveRoleAsync(result.User!);

        return TypedResults.Ok(new AuthResponse(
            result.Tokens!.AccessToken,
            result.Tokens.RefreshToken,
            result.Tokens.ExpiresInSeconds,
            ToAuthUser(result.User!, role)));
    }

    private static async Task<NoContent> LogoutAsync(
        LogoutRequest request,
        AppDbContext db,
        TokenService tokens,
        HttpContext http,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(http);

        var hash = string.IsNullOrEmpty(request.RefreshToken)
            ? null
            : Pos.Core.Security.OpaqueToken.Hash(request.RefreshToken);

        var stored = hash is null
            ? null
            : await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        // Only the token's owner may revoke its family. Otherwise any authenticated user
        // holding someone else's token string could log that person out.
        var subject = http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (stored is not null
            && Guid.TryParse(subject, CultureInfo.InvariantCulture, out var userId)
            && stored.UserId == userId)
        {
            await tokens.RevokeFamilyAsync(stored.FamilyId, timeProvider.GetUtcNow(), cancellationToken);
        }

        // 204 either way. Telling a caller their token was not found would confirm which
        // token strings exist, and a logout that reports failure is a logout users retry.
        return TypedResults.NoContent();
    }

    private static async Task<Results<Ok<MeResponse>, UnauthorizedHttpResult>> MeAsync(
        AppDbContext db,
        UserManager<ApplicationUser> users,
        TokenService tokens,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(http);

        var subject = http.User.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;

        if (!Guid.TryParse(subject, CultureInfo.InvariantCulture, out var userId))
        {
            return TypedResults.Unauthorized();
        }

        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            return TypedResults.Unauthorized();
        }

        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == user.TenantId, cancellationToken);

        if (tenant is null)
        {
            return TypedResults.Unauthorized();
        }

        var role = await tokens.ResolveRoleAsync(user);

        return TypedResults.Ok(new MeResponse(
            ToAuthUser(user, role),
            new TenantSettings(
                tenant.Slug,
                tenant.Name,
                tenant.CurrencyCode,
                tenant.TimeZoneId,
                tenant.TaxMode.ToString())));
    }

    /// <summary>
    /// The register the presented device token belongs to.
    /// </summary>
    /// <remarks>
    /// The token was validated by the authentication scheme before either handler was reached,
    /// so an unenrolled till never gets as far as presenting a PIN.
    /// </remarks>
    private static bool TryReadRegister(HttpContext http, out Guid registerId) =>
        Guid.TryParse(
            http.User.FindFirst(PosClaims.RegisterId)?.Value,
            CultureInfo.InvariantCulture,
            out registerId);

    /// <summary>
    /// Verifies a PIN, counting the attempt.
    /// </summary>
    /// <remarks>
    /// <b>Shared by <c>/auth/pin</c> and <c>/auth/override</c> on purpose.</b> Both are a PIN
    /// presented from a till, both must count failures toward the same lockout, and a second
    /// copy of this is how one of the two quietly stops doing so — leaving an unrated-limited
    /// guessing oracle behind the endpoint nobody was looking at.
    /// </remarks>
    /// <returns>The user, or the single failure response to return in their place.</returns>
    private static async Task<(ApplicationUser? User, ProblemHttpResult? Failure)> CheckPinAsync(
        UserManager<ApplicationUser> users,
        Guid userId,
        string pin,
        CancellationToken cancellationToken)
    {
        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

        if (user is null || !user.IsActive || user.PinHash is null)
        {
            return (null, InvalidCredentials());
        }

        if (await users.IsLockedOutAsync(user))
        {
            return (null, LockedOut(user.LockoutEnd));
        }

        var verification = users.PasswordHasher.VerifyHashedPassword(user, user.PinHash, pin);

        if (verification == PasswordVerificationResult.Failed)
        {
            await users.AccessFailedAsync(user);

            // Told plainly once the lockout has actually engaged: the staff member needs to
            // know to wait rather than keep trying, and by then the attempt budget is spent
            // anyway so it reveals nothing an attacker could not measure.
            return await users.IsLockedOutAsync(user)
                ? (null, LockedOut(await users.GetLockoutEndDateAsync(user)))
                : (null, InvalidCredentials());
        }

        await users.ResetAccessFailedCountAsync(user);

        return (user, null);
    }

    /// <summary>
    /// Stamps <c>LastLoginAt</c> without going through the change tracker.
    /// </summary>
    /// <remarks>
    /// Set-based, and that is the whole point. The obvious version —
    /// <c>user.LastLoginAt = now; await users.UpdateAsync(user);</c> — is broken under
    /// concurrency in a way that is invisible until two sessions start at the same instant:
    /// <para>
    /// <c>ApplicationUser</c> carries Identity's <c>ConcurrencyStamp</c>. Two simultaneous
    /// logins as the same account both load the row at stamp <i>S</i>. The first update wins
    /// and moves it to <i>S′</i>. The second matches nothing, and
    /// <c>UserManager.UpdateAsync</c> <b>does not throw</b> — it returns a failed
    /// <c>IdentityResult</c> that nothing was checking. The entity is then left in the
    /// tracker still <c>Modified</c>, so the very next <c>SaveChangesAsync</c> — the one in
    /// <c>TokenService.IssueForFamilyAsync</c> that only meant to insert a refresh token —
    /// retries it and throws <c>DbUpdateConcurrencyException</c>. The login fails with a 500,
    /// having already checked the password successfully.
    /// </para>
    /// <para>
    /// Found by the Phase 4 Playwright suite, which logs in from several workers at once.
    /// Two tills sharing an owner account, or one person double-clicking Sign in, would have
    /// reproduced it in a shop. <c>ConcurrentLoginTests</c> pins it.
    /// </para>
    /// <para>
    /// A last-login timestamp is a convenience. It must never be able to fail a login, so it
    /// is written as an UPDATE that names the row and carries no concurrency token. The
    /// tenant query filter still applies, and RLS covers it underneath.
    /// </para>
    /// </remarks>
    /// <returns>Rows updated. Ignored by both callers — see the remarks.</returns>
    private static Task<int> RecordLoginAsync(
        AppDbContext db,
        ApplicationUser user,
        DateTimeOffset now,
        CancellationToken cancellationToken) =>
        db.Users
            .Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.LastLoginAt, now), cancellationToken);

    private static async Task<AuthResponse> BuildAuthResponseAsync(
        TokenService tokens,
        ApplicationUser user,
        Guid? registerId,
        CancellationToken cancellationToken)
    {
        var issued = await tokens.IssueAsync(user, registerId, cancellationToken);
        var role = await tokens.ResolveRoleAsync(user);

        return new AuthResponse(issued.AccessToken, issued.RefreshToken, issued.ExpiresInSeconds, ToAuthUser(user, role));
    }

    private static AuthUser ToAuthUser(ApplicationUser user, string? role)
        => new(user.Id, user.DisplayName, role, PolicyCatalog.PoliciesFor(role));

    /// <summary>
    /// The single failure response for every unsuccessful login, after burning comparable
    /// time to a successful one.
    /// </summary>
    private static ProblemHttpResult RejectCredentials(UserManager<ApplicationUser> users, string? password)
    {
        if (password is not null)
        {
            // Deliberately discarded. The work, not the answer, is the point.
            _ = users.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), DecoyPasswordHash, password);
        }

        return InvalidCredentials();
    }

    private static ProblemHttpResult InvalidCredentials()
        => TypedResults.Problem(
            title: "Invalid credentials",
            detail: "The tenant, email address or password is incorrect.",
            statusCode: StatusCodes.Status401Unauthorized,
            type: "https://pos.example/errors/invalid-credentials");

    /// <summary>The PIN was right; the person it belongs to still cannot authorise this.</summary>
    private static ProblemHttpResult NotPermittedToAuthorize()
        => TypedResults.Problem(
            title: "Not permitted to authorise",
            detail: "That member of staff cannot authorise this. Ask a manager or the owner.",
            statusCode: StatusCodes.Status403Forbidden,
            type: "https://pos.example/errors/override-not-permitted");

    /// <summary>Carries the expiry, so the till can say "try again at 14:05" rather than "no".</summary>
    private static ProblemHttpResult LockedOut(DateTimeOffset? until)
        => TypedResults.Problem(
            title: "Account locked",
            detail: "Too many failed attempts. Try again later or ask a manager to reset the PIN.",
            statusCode: StatusCodes.Status401Unauthorized,
            type: "https://pos.example/errors/account-locked",
            extensions: new Dictionary<string, object?>
            {
                ["lockoutEndsAt"] = until,
            });
}
