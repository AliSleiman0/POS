using System.Globalization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Pos.Api.Auth;
using Pos.Api.Errors;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Core.Exceptions;
using Pos.Core.Security;
using Pos.Core.Tenancy;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Endpoints;

public sealed record SetPinRequest(string Pin);

public sealed record CreateEmployeeRequest(
    string DisplayName,
    string Email,
    string Role,
    string Password,
    string? Pin);

public sealed record UpdateEmployeeRequest(string DisplayName, string Role, bool IsActive);

/// <summary>One member of staff, as the people-management screen sees them.</summary>
/// <remarks>
/// No <c>PinHash</c> and no <c>PasswordHash</c>, obviously — but note also that there is no
/// "has this person been given a password" flag beyond <see cref="HasPin"/>. Everybody has a
/// password by construction: it is required to create them.
/// </remarks>
public sealed record EmployeeSummary(
    Guid Id,
    string DisplayName,
    string Email,
    string? Role,
    bool IsActive,
    bool HasPin,
    DateTimeOffset? LastLoginAt);

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

        // Not applied at the group level, unlike /registers: this one route authenticates a
        // device rather than a person, so the two need different schemes.
        employees.MapGet("/pin-eligible", PinEligibleAsync)
            .RequireAuthorization(DeviceTokenAuthenticationHandler.PolicyName)
            .WithSummary("Names for the PIN screen, from an enrolled till only");

        employees.MapGet("/", ListAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Every member of staff at this shop");

        employees.MapPost("/", CreateAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Add a member of staff");

        employees.MapPut("/{id:guid}", UpdateAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Change a member of staff's name, role or active status");

        employees.MapPost("/{id:guid}/deactivate", DeactivateAsync)
            .RequireAuthorization(Policies.CanManageEmployees)
            .WithSummary("Withdraw a member of staff's access");

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

    /// <remarks>
    /// A bare list, not a <c>CursorPage</c>. <c>CursorPaging.ToPageAsync</c> is constrained to
    /// <c>TenantEntity</c> and <see cref="ApplicationUser"/> is an <c>IdentityUser</c>, so it
    /// does not qualify — and relaxing that constraint to serve one endpoint would give up the
    /// <c>Id</c> the keyset tiebreaker depends on. A shop has tens of staff, <c>GET
    /// /registers</c> already answers this shape, and docs/API.md marks only <c>/audit</c> as
    /// paginated.
    /// <para>
    /// <b>Deactivated staff are included by default</b>, which is the opposite of the catalog's
    /// lists. There is no separate reactivate route — you reactivate somebody by editing them —
    /// so hiding them by default would make the only way back invisible.
    /// </para>
    /// </remarks>
    private static async Task<Ok<List<EmployeeSummary>>> ListAsync(
        AppDbContext db,
        bool? activeOnly,
        CancellationToken cancellationToken)
    {
        var query = db.Users.AsNoTracking();

        if (activeOnly ?? false)
        {
            query = query.Where(u => u.IsActive);
        }

        var employees = await query
            .OrderBy(u => u.DisplayName)
            .Select(u => new EmployeeSummary(
                u.Id,
                u.DisplayName,
                u.Email!,
                RoleOf(db, u.Id),
                u.IsActive,
                u.PinHash != null,
                u.LastLoginAt))
            .ToListAsync(cancellationToken);

        return TypedResults.Ok(employees);
    }

    private static async Task<Results<Created<EmployeeSummary>, ValidationProblem>> CreateAsync(
        CreateEmployeeRequest request,
        UserManager<ApplicationUser> users,
        AppDbContext db,
        IAuditLog audit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = ValidateCreate(request);

        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var email = request.Email.Trim();

        var user = new ApplicationUser
        {
            UserName = email,
            Email = email,
            DisplayName = request.DisplayName.Trim(),
        };

        // No TenantId set, and none accepted from the body — the interceptor stamps it from
        // the validated token (CLAUDE.md invariant 2).
        var created = await users.CreateAsync(user, request.Password);

        if (!created.Succeeded)
        {
            // Identity returns results rather than throwing, so an unchecked call fails
            // silently. Its messages are the only place that says *which* password rule was
            // broken, so they are surfaced rather than replaced with a generic sentence.
            return TypedResults.ValidationProblem(Describe(created));
        }

        var roled = await users.AddToRoleAsync(user, request.Role);

        if (!roled.Succeeded)
        {
            return TypedResults.ValidationProblem(Describe(roled));
        }

        if (request.Pin is { } pin)
        {
            user.PinHash = users.PasswordHasher.HashPassword(user, pin);
            await users.UpdateAsync(user);
        }

        // Staged then saved by the audit log itself: CreateAsync has already committed the
        // user through Identity's own save, so there is no open transaction left to join.
        // The gap is real but tiny and one-directional — a crash between the two leaves a
        // user with no creation entry, never an entry with no user.
        await audit.RecordStandaloneAsync(
            AuditAction.EmployeeCreated,
            nameof(ApplicationUser),
            user.Id,
            after: new Dictionary<string, string?>
            {
                ["displayName"] = user.DisplayName,
                ["email"] = user.Email,
                ["role"] = request.Role,
                ["hasPin"] = (request.Pin is not null).ToString(CultureInfo.InvariantCulture),
            },
            cancellationToken: cancellationToken);

        var summary = new EmployeeSummary(
            user.Id,
            user.DisplayName,
            user.Email!,
            request.Role,
            user.IsActive,
            HasPin: request.Pin is not null,
            LastLoginAt: null);

        return TypedResults.Created($"/api/v1/employees/{user.Id}", summary);
    }

    private static async Task<Results<Ok<EmployeeSummary>, NotFound, ValidationProblem>> UpdateAsync(
        Guid id,
        UpdateEmployeeRequest request,
        UserManager<ApplicationUser> users,
        AppDbContext db,
        ITenantContext tenant,
        ICurrentActor actor,
        IAuditLog audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = ValidateUpdate(request);

        // Validated before the row is looked up, deliberately: the isolation manifest's row
        // for this route sends a body that is valid in the *calling* tenant, and a malformed
        // one would be answered 400 before the cross-tenant lookup it exists to test ever ran.
        if (errors.Count > 0)
        {
            return TypedResults.ValidationProblem(errors);
        }

        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            // 404, not 403. The query filter has already hidden another tenant's user; a 403
            // would confirm the id exists somewhere.
            return TypedResults.NotFound();
        }

        var roleBefore = await CurrentRoleAsync(db, user.Id, cancellationToken);
        var wasActive = user.IsActive;

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            await GuardOwnershipAsync(
                db,
                tenant,
                actor,
                user.Id,
                losingOwnership: !string.Equals(request.Role, RoleNames.Owner, StringComparison.Ordinal),
                losingAccess: !request.IsActive,
                cancellationToken);

            user.DisplayName = request.DisplayName.Trim();
            user.IsActive = request.IsActive;

            if (!string.Equals(roleBefore, request.Role, StringComparison.Ordinal))
            {
                if (roleBefore is not null)
                {
                    await users.RemoveFromRoleAsync(user, roleBefore);
                }

                await users.AddToRoleAsync(user, request.Role);

                audit.Record(
                    AuditAction.RoleChanged,
                    nameof(ApplicationUser),
                    user.Id,
                    before: new Dictionary<string, string?> { ["role"] = roleBefore },
                    after: new Dictionary<string, string?> { ["role"] = request.Role });
            }

            if (wasActive && !request.IsActive)
            {
                await WithdrawAccessAsync(db, audit, user, timeProvider, cancellationToken);
            }

            // Identity saves through this same scoped context, so the role rows, the user row
            // and the audit entries above are all in this transaction already. This save is
            // what carries the DisplayName/IsActive edits.
            await users.UpdateAsync(user);

            await transaction.CommitAsync(cancellationToken);
        });

        return TypedResults.Ok(new EmployeeSummary(
            user.Id,
            user.DisplayName,
            user.Email!,
            request.Role,
            user.IsActive,
            user.PinHash != null,
            user.LastLoginAt));
    }

    private static async Task<Results<NoContent, NotFound>> DeactivateAsync(
        Guid id,
        UserManager<ApplicationUser> users,
        AppDbContext db,
        ITenantContext tenant,
        ICurrentActor actor,
        IAuditLog audit,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return TypedResults.NotFound();
        }

        if (!user.IsActive)
        {
            // Already done. 204 rather than a conflict: the caller asked for a state that
            // holds, and a second click on a slow connection is not an error.
            return TypedResults.NoContent();
        }

        var strategy = db.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            await GuardOwnershipAsync(
                db,
                tenant,
                actor,
                user.Id,
                losingOwnership: false,
                losingAccess: true,
                cancellationToken);

            user.IsActive = false;

            await WithdrawAccessAsync(db, audit, user, timeProvider, cancellationToken);

            await users.UpdateAsync(user);

            await transaction.CommitAsync(cancellationToken);
        });

        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound, ValidationProblem>> SetPinAsync(
        Guid id,
        SetPinRequest request,
        UserManager<ApplicationUser> users,
        IAuditLog audit,
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

        // Deliberately no check that the PIN is unused in this tenant. An error saying "that
        // PIN is taken" tells whoever asked a valid PIN for somebody else's account; identity
        // here is picking your own name *and* entering your PIN.
        var user = await users.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);

        if (user is null)
        {
            return TypedResults.NotFound();
        }

        // The password hasher, not a bare digest. Four digits is 10,000 possibilities, and
        // a fast hash of that keyspace is a sub-second job for anyone holding the table.
        user.PinHash = users.PasswordHasher.HashPassword(user, request.Pin);

        // Staged *before* the update, which is the whole reason this needs no transaction:
        // UserManager writes through the same scoped AppDbContext, so Identity's own
        // SaveChanges flushes the entry and the new hash in one implicit transaction. Staging
        // afterwards would need a second save and reopen the gap.
        //
        // No before/after payload: the old and new hashes are both secrets, and "{}" would
        // read as "we captured the change and it was empty".
        audit.Record(AuditAction.PinReset, nameof(ApplicationUser), user.Id);

        await users.UpdateAsync(user);

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Refuses any change that would leave the shop unable to manage itself.
    /// </summary>
    /// <remarks>
    /// <b>Must be called inside a transaction.</b> The <c>FOR UPDATE</c> below locks the owner
    /// rows it counted, so two concurrent removals cannot both read "there are two of us" and
    /// both proceed. A count taken outside a lock is right when taken and wrong when acted on,
    /// which is exactly the interleaving that empties a shop.
    /// <para>
    /// The self-checks come first and need no lock — they are comparisons against the caller's
    /// own id, and they give the more useful message when somebody is both the actor and the
    /// last owner.
    /// </para>
    /// </remarks>
    private static async Task GuardOwnershipAsync(
        AppDbContext db,
        ITenantContext tenant,
        ICurrentActor actor,
        Guid targetUserId,
        bool losingOwnership,
        bool losingAccess,
        CancellationToken cancellationToken)
    {
        if (!losingOwnership && !losingAccess)
        {
            return;
        }

        var isSelf = actor.UserId == targetUserId;

        if (isSelf && losingAccess)
        {
            throw new SelfDeactivationException();
        }

        var tenantId = tenant.TenantId;

        // Raw SQL because FOR UPDATE has no LINQ equivalent, so the tenant predicate is
        // written by hand — the same trade AssertShiftIsOpenAsync documents. Not a bypass of
        // invariant 2: the tenant still comes from the validated token, and RLS is underneath.
        //
        // AS "Value" is one word on purpose. UseSnakeCaseNamingConvention makes SqlQuery<T>
        // look up `owner_id` for AS "OwnerId" and fail with "the required column was not
        // present"; the lookup is case-insensitive, so only the underscore defeats it.
        //
        // Postgres locks rows from every table in the FROM unless you write FOR UPDATE OF, so
        // this holds both the user row and its role row — either flavour of removal conflicts.
        var activeOwnerIds = await db.Database
            .SqlQuery<Guid>(
                $"""
                 SELECT u.id AS "Value"
                 FROM application_user u
                 JOIN user_role ur ON ur.user_id = u.id AND ur.tenant_id = u.tenant_id
                 JOIN application_role r ON r.id = ur.role_id
                 WHERE u.tenant_id = {tenantId} AND u.is_active AND r.name = {RoleNames.Owner}
                 FOR UPDATE
                 """)
            .ToListAsync(cancellationToken);

        if (!activeOwnerIds.Contains(targetUserId))
        {
            // Not an owner, or already inactive. Nothing this guard protects is at stake.
            return;
        }

        if (isSelf && losingOwnership)
        {
            throw new SelfDemotionException();
        }

        if (activeOwnerIds.Count <= 1)
        {
            throw new LastOwnerException();
        }
    }

    /// <summary>
    /// Withdraws a deactivated user's live sessions, and records the withdrawal.
    /// </summary>
    /// <remarks>
    /// <c>IsActive</c> is checked at login, at PIN entry, on <c>/auth/me</c> and on refresh —
    /// but <b>not</b> while validating an already-issued access token. Revoking the refresh
    /// tokens here closes the long door; the short one stays open for up to the access token's
    /// ~15 minutes, and that residual window is stated in docs/API.md rather than pretended
    /// away. Closing it needs a per-request liveness read or a token-version claim, neither of
    /// which this phase takes on.
    /// </remarks>
    private static async Task WithdrawAccessAsync(
        AppDbContext db,
        IAuditLog audit,
        ApplicationUser user,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        // ExecuteUpdateAsync runs immediately and enlists in the ambient transaction, which is
        // why this is only ever called from inside one. It bypasses the change tracker, and
        // therefore the stamping interceptor — harmless here, because nothing is being
        // stamped, the query filter still applies and RLS is underneath.
        await db.RefreshTokens
            .Where(t => t.UserId == user.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.RevokedAt, timeProvider.GetUtcNow()),
                cancellationToken);

        audit.Record(
            AuditAction.EmployeeDeactivated,
            nameof(ApplicationUser),
            user.Id,
            before: new Dictionary<string, string?> { ["isActive"] = "True" },
            after: new Dictionary<string, string?> { ["isActive"] = "False" });
    }

    /// <summary>
    /// This user's role, as a correlated subquery rather than a join.
    /// </summary>
    /// <remarks>
    /// A join would return one row per role and duplicate a user who somehow held two. Nothing
    /// in the application assigns a second role, and this is the shape that keeps the list
    /// honest if something ever does — one row per person, in one statement, with no N+1.
    /// </remarks>
    private static string? RoleOf(AppDbContext db, Guid userId) =>
        db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .FirstOrDefault();

    private static Task<string?> CurrentRoleAsync(
        AppDbContext db,
        Guid userId,
        CancellationToken cancellationToken) =>
        db.UserRoles
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name)
            .FirstOrDefaultAsync(cancellationToken);

    private static Dictionary<string, string[]> ValidateCreate(CreateEmployeeRequest request)
    {
        var errors = ValidateNameAndRole(request.DisplayName, request.Role);

        if (string.IsNullOrWhiteSpace(request.Email)
            || request.Email.Length > ApplicationUser.EmailMaxLength
            || !request.Email.Contains('@', StringComparison.Ordinal))
        {
            errors["email"] = ["An email address is required."];
        }

        if (string.IsNullOrEmpty(request.Password))
        {
            // Length and complexity are Identity's to judge, and its messages name the rule
            // that was broken. Only emptiness is checked here, so the caller is not told
            // "required" and "too short" as though they were different problems.
            errors["password"] = ["A password is required."];
        }

        if (request.Pin is { } pin && !Pin.IsWellFormed(pin))
        {
            errors["pin"] = [$"A PIN is {Pin.MinLength} to {Pin.MaxLength} digits."];
        }

        return errors;
    }

    private static Dictionary<string, string[]> ValidateUpdate(UpdateEmployeeRequest request) =>
        ValidateNameAndRole(request.DisplayName, request.Role);

    private static Dictionary<string, string[]> ValidateNameAndRole(string displayName, string role)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(displayName)
            || displayName.Length > ApplicationUser.DisplayNameMaxLength)
        {
            errors["displayName"] =
                [$"A name of 1 to {ApplicationUser.DisplayNameMaxLength} characters is required."];
        }

        if (!RoleNames.All.Contains(role, StringComparer.Ordinal))
        {
            errors["role"] = [$"Role must be one of: {string.Join(", ", RoleNames.All)}."];
        }

        return errors;
    }

    /// <summary>Turns an Identity failure into the same errors map hand-validation produces.</summary>
    private static Dictionary<string, string[]> Describe(IdentityResult result)
    {
        var byField = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var error in result.Errors)
        {
            // Identity's codes are its own vocabulary; these are the ones reachable from here.
            // Anything unrecognised lands on the password field rather than being dropped —
            // a message nobody can see is worse than one filed slightly wrong.
            var field = error.Code switch
            {
                "DuplicateEmail" or "InvalidEmail" or "DuplicateUserName" or "InvalidUserName" => "email",
                _ => "password",
            };

            if (!byField.TryGetValue(field, out var messages))
            {
                messages = [];
                byField[field] = messages;
            }

            messages.Add(error.Description);
        }

        return byField.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
    }
}
