using Microsoft.AspNetCore.Identity;

namespace Pos.Data.Identity;

/// <summary>
/// One of the three platform roles: <c>Cashier</c>, <c>Manager</c>, <c>Owner</c>.
/// </summary>
/// <remarks>
/// Deliberately <b>not</b> tenant-owned. A role row holds a name and nothing else, so
/// there is no tenant data in this table to leak, and giving every tenant its own copy of
/// three fixed strings would buy nothing but rows.
/// <para>
/// What a role is <i>allowed to do</i> is not stored here at all — it is the policy map in
/// <c>Pos.Api/Auth</c>, mirroring docs/ARCHITECTURE.md#authorization. Endpoints require
/// named policies, never role literals, so changing who may issue refunds is one edit
/// rather than an audit of every endpoint.
/// </para>
/// </remarks>
public sealed class ApplicationRole : IdentityRole<Guid>
{
    public ApplicationRole()
    {
    }

    public ApplicationRole(string roleName)
        : base(roleName)
    {
    }
}

/// <summary>The three role names, in one place so nothing spells them by hand.</summary>
public static class RoleNames
{
    public const string Cashier = "Cashier";
    public const string Manager = "Manager";
    public const string Owner = "Owner";

    /// <summary>Every role, for seeding and for the policy-map test.</summary>
    public static IReadOnlyList<string> All { get; } = [Cashier, Manager, Owner];
}
