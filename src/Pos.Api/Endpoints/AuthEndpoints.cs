using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.JsonWebTokens;
using Pos.Api.Auth;
using Pos.Core.Entities;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Endpoints;

public sealed record LoginRequest(string TenantSlug, string Email, string Password);

public sealed record PinLoginRequest(Guid UserId, string Pin);

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

        user.LastLoginAt = timeProvider.GetUtcNow();
        await users.UpdateAsync(user);

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

        // The device token was validated by the authentication scheme before this method
        // was reached, so an unenrolled till never gets as far as presenting a PIN.
        var registerClaim = http.User.FindFirst(PosClaims.RegisterId)?.Value;

        if (!Guid.TryParse(registerClaim, CultureInfo.InvariantCulture, out var registerId))
        {
            return InvalidCredentials();
        }

        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == request.UserId, cancellationToken);

        if (user is null || !user.IsActive || user.PinHash is null)
        {
            return InvalidCredentials();
        }

        if (await users.IsLockedOutAsync(user))
        {
            return LockedOut(user.LockoutEnd);
        }

        var verification = users.PasswordHasher.VerifyHashedPassword(user, user.PinHash, request.Pin);

        if (verification == PasswordVerificationResult.Failed)
        {
            await users.AccessFailedAsync(user);

            // Told plainly once the lockout has actually engaged: the staff member needs to
            // know to wait rather than keep trying, and by then the attempt budget is spent
            // anyway so it reveals nothing an attacker could not measure.
            return await users.IsLockedOutAsync(user)
                ? LockedOut((await users.GetLockoutEndDateAsync(user)))
                : InvalidCredentials();
        }

        await users.ResetAccessFailedCountAsync(user);

        var now = timeProvider.GetUtcNow();
        user.LastLoginAt = now;
        await users.UpdateAsync(user);

        var register = await db.Registers.FirstOrDefaultAsync(r => r.Id == registerId, cancellationToken);

        if (register is not null)
        {
            // Answers "is that lost tablet still being used?" without a write on every request.
            register.LastSeenAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.Ok(await BuildAuthResponseAsync(tokens, user, registerId, cancellationToken));
    }

    private static async Task<Results<Ok<AuthResponse>, ProblemHttpResult>> RefreshAsync(
        RefreshRequest request,
        TokenService tokens,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var result = await tokens.RotateAsync(request.RefreshToken, cancellationToken);

        if (!result.Succeeded)
        {
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
