using Pos.Data.Identity;

namespace Pos.Api.Tests.Infrastructure;

/// <summary>
/// One tenant, all three roles, and a catalog that tests may change.
/// </summary>
/// <remarks>
/// <b>Assert what is present, never how many things are.</b> Every test sharing this tenant
/// creates rows in it and none of them clean up, so a count assertion here would depend on
/// the order xUnit happened to run the classes in — which is not fixed. That is exactly the
/// rule <see cref="Isolation.TwoTenantWorld"/> enforces by being read-only; this one takes
/// the other side of the trade and buys speed with it.
/// <para>
/// SKUs and names created by a test must therefore be unique to that test. The seeded rows
/// below are the ones every test can rely on existing.
/// </para>
/// </remarks>
public sealed class CatalogSandbox
{
    public const string Slug = "catalog-sandbox";
    public const string Password = "Correct-Horse-9";

    public const string OwnerEmail = "owner@sandbox.test";
    public const string ManagerEmail = "manager@sandbox.test";
    public const string CashierEmail = "cashier@sandbox.test";

    /// <summary>
    /// Display names that avoid the words Owner, Manager and Cashier.
    /// </summary>
    /// <remarks>
    /// Same reason the dev seeder does it: a test asserting a role name is absent from a
    /// response would pass or fail on the fixture's prose rather than on the endpoint.
    /// </remarks>
    public const string OwnerName = "Ada Byrne";
    public const string ManagerName = "Sam Cole";
    public const string CashierName = "Robin Vale";

    public required Guid TenantId { get; init; }

    public required Guid OwnerId { get; init; }

    /// <summary>
    /// Holds <c>CanManageCatalog</c> but <b>not</b> <c>CanViewMargins</c>.
    /// </summary>
    /// <remarks>
    /// The single most useful actor in this milestone. A Manager may create and edit
    /// products yet must never see <c>costPrice</c> — which makes them the caller that
    /// proves the omission is real, and the caller whose round-trip edit must not wipe the
    /// Owner's cost data.
    /// </remarks>
    public required Guid ManagerId { get; init; }

    public required Guid CashierId { get; init; }

    /// <summary>The rows seeded here. Tests add to the tenant; they do not change these.</summary>
    public required SeededCatalog Catalog { get; init; }

    internal static async Task<CatalogSandbox> SeedAsync(PosApiFactory factory)
    {
        var tenant = await factory.CreateTenantAsync(Slug, "Corner Shop");

        var owner = await factory.CreateUserAsync(
            tenant.Id, OwnerEmail, Password, RoleNames.Owner, OwnerName);

        var manager = await factory.CreateUserAsync(
            tenant.Id, ManagerEmail, Password, RoleNames.Manager, ManagerName);

        var cashier = await factory.CreateUserAsync(
            tenant.Id, CashierEmail, Password, RoleNames.Cashier, CashierName);

        return new CatalogSandbox
        {
            TenantId = tenant.Id,
            OwnerId = owner.Id,
            ManagerId = manager.Id,
            CashierId = cashier.Id,
            Catalog = await CatalogFixture.WriteAsync(factory, tenant.Id),
        };
    }
}
