using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Core.Security;
using Pos.Data.Identity;

namespace Pos.Api.Endpoints;

public sealed record SetPinRequest(string Pin);

/// <summary>
/// Everything the PIN screen is allowed to know about a member of staff.
/// </summary>
/// <remarks>
/// Id and display name, and nothing else. This list is readable from a device sitting on a
/// counter, so it must not become a way to lift the staff roster with roles and email
/// addresses. Fields a caller may not see are absent from the response, not hidden by the
/// client — see CLAUDE.md invariant 7.
/// </remarks>
public sealed record PinEligibleEmployee(Guid Id, string DisplayName);

public static class EmployeeEndpoints
{
    public static IEndpointRouteBuilder MapEmployeeEndpoints(this IEndpointRouteBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var employees = builder.MapGroup("/api/v1/employees").WithTags("Employees");

        employees.MapGet("/pin-eligible", PinEligibleAsync)
            .RequireAuthorization(DeviceTokenAuthenticationHandler.PolicyName)
            .WithSummary("Names for the PIN screen, from an enrolled till only");

        employees.MapPost("/{id:guid}/set-pin", SetPinAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Set or replace a cashier's PIN");

        return builder;
    }

    private static async Task<Ok<List<PinEligibleEmployee>>> PinEligibleAsync(
        UserManager<ApplicationUser> users,
        CancellationToken cancellationToken)
    {
        var employees = await users.Users
            .Where(u => u.IsActive && u.PinHash != null)
            .OrderBy(u => u.DisplayName)
            // Projected in the query, so the columns never leave the database rather than
            // being fetched and then dropped on the way out.
            .Select(u => new PinEligibleEmployee(u.Id, u.DisplayName))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(employees);
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetPinAsync(
        Guid id,
        SetPinRequest request,
        UserManager<ApplicationUser> users,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!Pin.IsWellFormed(request.Pin))
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            {
                ["pin"] = [$"A PIN is {Pin.MinLength} to {Pin.MaxLength} digits."],
            });
        }

        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return TypedResults.NotFound();
        }

        // The password hasher, not a bare digest. Four digits is 10,000 possibilities, and
        // a fast hash of that keyspace is a sub-second job for anyone holding the table.
        user.PinHash = users.PasswordHasher.HashPassword(user, request.Pin);

        await users.UpdateAsync(user);

        return TypedResults.NoContent();
    }
}
