using Pos.Api.Tests.Infrastructure;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Isolation;

/// <summary>One tenant's fixtures. Identical in both tenants except for the id and the slug.</summary>
public sealed record IsolatedTenant(
    Guid Id,
    string Slug,
    Guid OwnerId,
    Guid CashierId,
    Guid SecondCashierId,
    EnrolledRegister FrontCounter,
    Guid BackCounterId,
    SeededCatalog Catalog,
    SeededSales Sales)
{
    /// <summary>Everything <c>GET /registers</c> must return for this tenant, and nothing else.</summary>
    public IReadOnlyList<Guid> RegisterIds => [FrontCounter.Id, BackCounterId];

    /// <summary>Everything <c>GET /employees/pin-eligible</c> must return: staff with a PIN.</summary>
    public IReadOnlyList<Guid> PinEligibleIds => [CashierId, SecondCashierId];

    /// <summary>Everything <c>GET /products</c> must return. All three are active.</summary>
    /// <remarks>
    /// Active on purpose. <c>?activeOnly=</c> defaults to <c>true</c>, so a deactivated
    /// fixture would have to be excluded from this list — and a fixture with a carve-out is
    /// one people get wrong. The filter's behaviour is tested against the sandbox tenant,
    /// where rows can be deactivated freely.
    /// </remarks>
    public IReadOnlyList<Guid> ProductIds => Catalog.ProductIds;

    /// <summary>Everything <c>GET /categories</c> must return.</summary>
    public IReadOnlyList<Guid> CategoryIds => Catalog.CategoryIds;

    /// <summary>Everything <c>GET /stock</c> must return: the products that track stock.</summary>
    public IReadOnlyList<Guid> StockedProductIds => Catalog.StockedProductIds;

    /// <summary>Everything <c>GET /tax-classes</c> must return.</summary>
    public IReadOnlyList<Guid> TaxClassIds => Catalog.TaxClassIds;

    /// <summary>Everything <c>GET /sales</c> must return: completed, voided and refunded alike.</summary>
    public IReadOnlyList<Guid> SaleIds => Sales.SaleIds;

    /// <summary>Everything <c>GET /stock/discrepancies</c> must return.</summary>
    public IReadOnlyList<Guid> DiscrepancyIds => Sales.DiscrepancyIds;
}

/// <summary>
/// Two tenants seeded with deliberately identical data, so that a leak doubles a list
/// rather than having to be reasoned about from ids.
/// </summary>
/// <remarks>
/// Same emails, same display names, same till names, same PINs, same password. Only the
/// slug differs, because the slug is the selector a caller uses to choose a tenant at
/// login — everything else being equal is what makes "returns exactly two" a real
/// assertion. If isolation broke, every collection here would come back with four rows.
/// <para>
/// <b>The world is read-only.</b> No test adds to or removes from these collections. The
/// cross-tenant attempts in this suite are all supposed to fail, so they cannot mutate it;
/// the one test that genuinely creates a row creates its own throwaway tenants. Without
/// that rule the exact-count assertions would depend on the order xUnit happens to run the
/// classes in, which is not fixed.
/// </para>
/// </remarks>
public sealed class TwoTenantWorld
{
    public const string Password = "Correct-Horse-9";

    public const string OwnerEmail = "owner@isolation.test";
    public const string CashierEmail = "robin@isolation.test";
    public const string SecondCashierEmail = "jules@isolation.test";

    public const string OwnerName = "Pat Keeper";
    public const string CashierName = "Robin Vale";
    public const string SecondCashierName = "Jules Nord";

    public const string CashierPin = "4821";

    public const string FrontCounterName = "Front Counter";
    public const string BackCounterName = "Back Counter";

    /// <summary>The victim: every cross-tenant attempt in this suite reaches for A's rows.</summary>
    public required IsolatedTenant A { get; init; }

    /// <summary>The attacker's own tenant: every client in this suite authenticates into B.</summary>
    public required IsolatedTenant B { get; init; }

    internal static async Task<TwoTenantWorld> SeedAsync(PosApiFactory factory) => new()
    {
        A = await SeedTenantAsync(factory, "iso-a"),
        B = await SeedTenantAsync(factory, "iso-b"),
    };

    private static async Task<IsolatedTenant> SeedTenantAsync(PosApiFactory factory, string slug)
    {
        var tenant = await factory.CreateTenantAsync(slug, "Corner Shop");

        // The owner deliberately has no PIN: pin-eligible must return the two cashiers and
        // not everybody with an account.
        var owner = await factory.CreateUserAsync(
            tenant.Id, OwnerEmail, Password, RoleNames.Owner, OwnerName);

        var cashier = await factory.CreateUserAsync(
            tenant.Id, CashierEmail, Password, RoleNames.Cashier, CashierName);

        var second = await factory.CreateUserAsync(
            tenant.Id, SecondCashierEmail, Password, RoleNames.Cashier, SecondCashierName);

        await factory.SetPinAsync(tenant.Id, cashier.Id, CashierPin);
        await factory.SetPinAsync(tenant.Id, second.Id, CashierPin);

        // One enrolled and one not, so the list endpoint has something to get wrong beyond
        // the row count.
        var front = await factory.CreateEnrolledRegisterAsync(tenant.Id, FrontCounterName);
        var back = await factory.CreateRegisterAsync(tenant.Id, BackCounterName);

        // Identical in both tenants, same as everything else here. No users are added with
        // it, so PinEligibleIds is unaffected and the pin-eligible count assertions stand.
        var catalog = await CatalogFixture.WriteAsync(factory, tenant.Id);

        // A fixed trading history, so GET /sales and GET /stock/discrepancies have exactly the
        // same shape in both tenants. Seeded through the DbContext rather than the endpoints
        // under test, and never written to again — see SalesFixture.
        var sales = await SalesFixture.WriteAsync(factory, tenant.Id, front.Id, cashier.Id, catalog);

        return new IsolatedTenant(
            tenant.Id, slug, owner.Id, cashier.Id, second.Id, front, back, catalog, sales);
    }
}
