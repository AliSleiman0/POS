using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Core.Entities;
using Pos.Core.Security;
using Pos.Data;

namespace Pos.Api.Endpoints;

public sealed record CreateRegisterRequest(string Name);

public sealed record RegisterSummary(Guid Id, string Name, bool IsActive, bool IsEnrolled, DateTimeOffset? LastSeenAt);

/// <summary>The device token, returned exactly once.</summary>
public sealed record EnrollmentResponse(Guid RegisterId, string DeviceToken);

public static class RegisterEndpoints
{
    public static IEndpointRouteBuilder MapRegisterEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var registers = builder.MapGroup("/api/v1/registers")
            .WithTags("Registers")
            .RequireAuthorization(Policies.CanManageEmployees);

        registers.MapGet("/", ListAsync)
            .WithSummary("Every till in this tenant");

        registers.MapPost("/", CreateAsync)
            .WithSummary("Create a till (not yet enrolled)");

        registers.MapPost("/{id:guid}/enroll", EnrollAsync)
            .WithSummary("Issue a device token — shown once");

        registers.MapPost("/{id:guid}/revoke", RevokeAsync)
            .WithSummary("Invalidate a till's device token");

        return builder;
    }

    private static async Task<Ok<List<RegisterSummary>>> ListAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var registers = await db.Registers
            .OrderBy(r => r.Name)
            .Select(r => new RegisterSummary(r.Id, r.Name, r.IsActive, r.DeviceTokenHash != null && r.IsActive, r.LastSeenAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(registers);
    }

    private static async Task<Results<Created<RegisterSummary>, ValidationProblem>> CreateAsync(
        CreateRegisterRequest request,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > Register.NameMaxLength)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["name"] = [$"A name of 1 to {Register.NameMaxLength} characters is required."],
            });
        }

        // No TenantId set here, and none accepted from the request. The interceptor stamps
        // it from the validated token; see CLAUDE.md invariant 2.
        var register = new Register { Name = request.Name.Trim() };

        db.Registers.Add(register);
        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Created(
            $"/api/v1/registers/{register.Id}",
            new RegisterSummary(register.Id, register.Name, register.IsActive, IsEnrolled: false, LastSeenAt: null));
    }

    private static async Task<Results<Ok<EnrollmentResponse>, NotFound>> EnrollAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var register = await db.Registers.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (register is null)
        {
            // 404 rather than 403 for a register in another tenant. The query filter has
            // already made it invisible; a 403 would confirm the id exists somewhere.
            return TypedResults.NotFound();
        }

        var token = OpaqueToken.Issue(register.TenantId);

        // Only the hash is kept. Re-enrolling replaces the old token, which is also how a
        // till that lost its token gets a new one — there is no way to read the old one back.
        register.DeviceTokenHash = OpaqueToken.Hash(token);
        register.IsActive = true;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.Ok(new EnrollmentResponse(register.Id, token));
    }

    private static async Task<Results<NoContent, NotFound>> RevokeAsync(
        Guid id,
        AppDbContext db,
        CancellationToken cancellationToken)
    {
        var register = await db.Registers.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);

        if (register is null)
        {
            return TypedResults.NotFound();
        }

        // Clearing the hash is the revocation. The row stays, because sales reference it.
        register.DeviceTokenHash = null;

        await db.SaveChangesAsync(cancellationToken);

        return TypedResults.NoContent();
    }
}
