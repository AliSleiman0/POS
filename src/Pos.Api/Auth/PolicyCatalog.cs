using Pos.Data.Identity;

namespace Pos.Api.Auth;

/// <summary>Policy names. Endpoints reference these; nothing spells one by hand.</summary>
public static class Policies
{
    public const string CanSell = "CanSell";
    public const string CanApplyDiscount = "CanApplyDiscount";
    public const string CanOverridePrice = "CanOverridePrice";
    public const string CanVoidSale = "CanVoidSale";
    public const string CanRefund = "CanRefund";
    public const string CanManageCatalog = "CanManageCatalog";
    public const string CanViewMargins = "CanViewMargins";
    public const string CanManageEmployees = "CanManageEmployees";
    public const string CanCloseShift = "CanCloseShift";
}

/// <summary>
/// The single mapping from policy to the roles that satisfy it.
/// </summary>
/// <remarks>
/// Mirrors the table in docs/ARCHITECTURE.md#authorization, and a test fails if the two
/// disagree.
/// <para>
/// Endpoints require a <i>named policy</i> and never a role literal. "Can my supervisors
/// issue refunds?" is then one edit here, not an audit of every endpoint — and the answer
/// cannot differ between two endpoints that both meant "a manager".
/// </para>
/// <para>
/// The same list is returned by <c>GET /auth/me</c> so the UI can grey out controls it
/// cannot use. That gating is a courtesy: the server re-checks every call, because a
/// disabled button is a suggestion.
/// </para>
/// </remarks>
public static class PolicyCatalog
{
    private static readonly string[] Everyone = [RoleNames.Cashier, RoleNames.Manager, RoleNames.Owner];
    private static readonly string[] Supervisors = [RoleNames.Manager, RoleNames.Owner];
    private static readonly string[] OwnerOnly = [RoleNames.Owner];

    /// <summary>Which roles satisfy each policy.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> RolesByPolicy { get; } =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            [Policies.CanSell] = Everyone,
            [Policies.CanApplyDiscount] = Supervisors,
            [Policies.CanOverridePrice] = Supervisors,
            [Policies.CanVoidSale] = Supervisors,
            [Policies.CanRefund] = Supervisors,
            [Policies.CanManageCatalog] = Supervisors,
            [Policies.CanCloseShift] = Supervisors,

            // Margin data is the owner's commercial position. A manager who can see cost
            // prices can price-shop the shop's suppliers.
            [Policies.CanViewMargins] = OwnerOnly,

            // Whoever can create users and set PINs can create a user that sells.
            [Policies.CanManageEmployees] = OwnerOnly,
        };

    /// <summary>Every policy a role satisfies, for <c>GET /auth/me</c>.</summary>
    public static IReadOnlyList<string> PoliciesFor(string? role)
    {
        if (string.IsNullOrEmpty(role))
        {
            return [];
        }

        return [.. RolesByPolicy
            .Where(pair => pair.Value.Contains(role, StringComparer.Ordinal))
            .Select(pair => pair.Key)
            .Order(StringComparer.Ordinal)];
    }
}
