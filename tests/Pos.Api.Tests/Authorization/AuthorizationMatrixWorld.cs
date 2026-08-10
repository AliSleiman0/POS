using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Authorization;

/// <summary>One tenant holding one of every role, plus an enrolled till.</summary>
/// <remarks>
/// Its own world rather than <c>TwoTenantWorld</c>, for two reasons. That world is documented
/// as read-only with exact-count assertions, so anything probing it risks turning a count into
/// a coin toss; and it has no Manager at all, which is precisely the role this matrix exists to
/// pin down — <c>CanViewMargins</c> and <c>CanManageEmployees</c> are the two policies a
/// Manager does <i>not</i> hold, and both are the sort a customer notices only when it leaks.
/// </remarks>
public sealed class AuthorizationMatrixWorld
{
    public const string Password = "Correct-Horse-9";

    public required string Slug { get; init; }

    public required Guid TenantId { get; init; }

    /// <summary>The enrolled till's token, so the device scheme is a testable caller too.</summary>
    public required string DeviceToken { get; init; }

    public static string EmailFor(string role) => $"{role.ToLowerInvariant()}@matrix.test";

    internal static async Task<AuthorizationMatrixWorld> SeedAsync(PosApiFactory factory)
    {
        var slug = $"matrix-{Guid.CreateVersion7():N}"[..24];
        var tenant = await factory.CreateTenantAsync(slug, "Matrix Shop");

        foreach (var role in RoleNames.All)
        {
            await factory.CreateUserAsync(tenant.Id, EmailFor(role), Password, role, $"{role} Person");
        }

        var till = await factory.CreateEnrolledRegisterAsync(tenant.Id, "Matrix Till");

        return new AuthorizationMatrixWorld
        {
            Slug = slug,
            TenantId = tenant.Id,
            DeviceToken = till.DeviceToken,
        };
    }
}
