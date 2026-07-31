using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pos.Core.Entities;
using Pos.Core.Security;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Auth;

/// <summary>Claim names carried by an access token.</summary>
public static class PosClaims
{
    public const string TenantId = "tenant_id";
    public const string Role = "role";

    /// <summary>Present only for a session started by PIN on an enrolled register.</summary>
    public const string RegisterId = "register_id";
}

/// <summary>An issued token pair, ready to hand to a client.</summary>
public sealed record AuthTokens(string AccessToken, string RefreshToken, int ExpiresInSeconds);

/// <summary>The result of exchanging a refresh token.</summary>
public sealed record RefreshResult(AuthTokens? Tokens, ApplicationUser? User)
{
    public static RefreshResult Rejected { get; } = new(null, null);

    public bool Succeeded => Tokens is not null && User is not null;
}

/// <summary>
/// Issues access tokens and manages the refresh-token family lifecycle.
/// </summary>
public sealed class TokenService(
    AppDbContext db,
    UserManager<ApplicationUser> users,
    AmbientTenantContext tenantContext,
    TimeProvider timeProvider,
    IOptions<JwtOptions> options)
{
    private readonly JwtOptions _options = options.Value;

    /// <summary>Starts a new session: a fresh access token and a new refresh-token family.</summary>
    public async Task<AuthTokens> IssueAsync(ApplicationUser user, Guid? registerId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);

        var role = await ResolveRoleAsync(user);

        return await IssueForFamilyAsync(user, role, registerId, Guid.CreateVersion7(timeProvider.GetUtcNow()), cancellationToken);
    }

    /// <summary>
    /// Exchanges a refresh token for a new pair, rotating the old one.
    /// </summary>
    /// <remarks>
    /// Presenting a token that has already been rotated means two parties hold tokens from
    /// the same chain, which only happens if one copied it. The whole family is revoked:
    /// the legitimate user gets one unexpected logout, and the thief loses a session they
    /// could otherwise have refreshed indefinitely.
    /// </remarks>
    public async Task<RefreshResult> RotateAsync(string? presentedToken, CancellationToken cancellationToken)
    {
        if (!OpaqueToken.TryReadTenant(presentedToken, out var tenantId))
        {
            return RefreshResult.Rejected;
        }

        // The prefix only chooses which tenant to search. The hash below is what authenticates.
        tenantContext.Resolve(tenantId);

        var hash = OpaqueToken.Hash(presentedToken!);
        var stored = await db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);

        if (stored is null)
        {
            return RefreshResult.Rejected;
        }

        var now = timeProvider.GetUtcNow();

        if (!stored.IsActive(now))
        {
            // Expiry is ordinary; a spent or revoked token being presented is not.
            if (stored.ReplacedByTokenId is not null || stored.RevokedAt is not null)
            {
                await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            }

            return RefreshResult.Rejected;
        }

        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == stored.UserId, cancellationToken);

        if (user is null || !user.IsActive)
        {
            // A deactivated employee keeps a working refresh token until it expires
            // otherwise — which is the whole point of deactivating them, undone.
            await RevokeFamilyAsync(stored.FamilyId, now, cancellationToken);
            return RefreshResult.Rejected;
        }

        var role = await ResolveRoleAsync(user);

        var tokens = await IssueForFamilyAsync(
            user, role, stored.RegisterId, stored.FamilyId, cancellationToken, replacing: stored);

        return new RefreshResult(tokens, user);
    }

    /// <summary>Revokes every token descended from one login.</summary>
    public async Task RevokeFamilyAsync(Guid familyId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await db.RefreshTokens
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(set => set.SetProperty(t => t.RevokedAt, now), cancellationToken);
    }

    /// <summary>The role name for <paramref name="user"/>, or null if somehow unassigned.</summary>
    public async Task<string?> ResolveRoleAsync(ApplicationUser user)
    {
        var roles = await users.GetRolesAsync(user);

        // One role per user by design. If data ever says otherwise, take the least
        // privileged rather than the first the database happened to return.
        return RoleNames.All.FirstOrDefault(roles.Contains);
    }

    private async Task<AuthTokens> IssueForFamilyAsync(
        ApplicationUser user,
        string? role,
        Guid? registerId,
        Guid familyId,
        CancellationToken cancellationToken,
        RefreshToken? replacing = null)
    {
        var now = timeProvider.GetUtcNow();

        var refreshTokenValue = OpaqueToken.Issue(user.TenantId);

        var refreshToken = new RefreshToken
        {
            Id = Guid.CreateVersion7(now),
            UserId = user.Id,
            TokenHash = OpaqueToken.Hash(refreshTokenValue),
            FamilyId = familyId,
            RegisterId = registerId,
            ExpiresAt = now.AddDays(_options.RefreshTokenDays),
        };

        db.RefreshTokens.Add(refreshToken);

        if (replacing is not null)
        {
            replacing.RevokedAt = now;
            replacing.ReplacedByTokenId = refreshToken.Id;
        }

        await db.SaveChangesAsync(cancellationToken);

        var accessToken = CreateAccessToken(user, role, registerId, now);

        return new AuthTokens(accessToken, refreshTokenValue, _options.AccessTokenMinutes * 60);
    }

    private string CreateAccessToken(ApplicationUser user, string? role, Guid? registerId, DateTimeOffset now)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.CreateVersion7(now).ToString()),
            new(PosClaims.TenantId, user.TenantId.ToString()),
        };

        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(PosClaims.Role, role));
        }

        if (registerId is { } register)
        {
            claims.Add(new Claim(PosClaims.RegisterId, register.ToString()));
        }

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SigningKey));

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.AddMinutes(_options.AccessTokenMinutes).UtcDateTime,
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        };

        return new JsonWebTokenHandler().CreateToken(descriptor);
    }
}
