using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Core.Security;
using Pos.Data;
using Pos.Data.Identity;
using Pos.Core.Monetary;

namespace Pos.Api.Tests.Isolation;

/// <summary>How an endpoint is attacked across a tenant boundary.</summary>
public enum IsolationKind
{
    /// <summary>Returns a list. As tenant B it must return B's rows and only B's rows.</summary>
    Collection,

    /// <summary>Takes an id. Given tenant A's id, as tenant B, it must answer 404 — not 403.</summary>
    ById,

    /// <summary>Neither, for a stated reason. <see cref="IsolationCase.Exemption"/> says which.</summary>
    Exempt,
}

/// <summary>Who is making the request.</summary>
public enum Actor
{
    /// <summary>No credentials at all.</summary>
    Anonymous,

    /// <summary>Tenant B's owner, holding every policy.</summary>
    OwnerOfB,

    /// <summary>Tenant B's cashier, holding only <c>CanSell</c>.</summary>
    CashierOfB,

    /// <summary>Tenant B's enrolled till, which has a tenant but no user and no role.</summary>
    DeviceOfB,
}

/// <summary>One endpoint's entry in the manifest.</summary>
public sealed record IsolationCase
{
    /// <summary>
    /// <c>"POST api/v1/registers/{id:guid}/enroll"</c> — the HTTP method and the route
    /// pattern exactly as the router reports it, with the slashes trimmed.
    /// </summary>
    public required string Key { get; init; }

    public required IsolationKind Kind { get; init; }

    /// <summary>The caller that performs the cross-tenant attempt.</summary>
    public Actor Caller { get; init; } = Actor.OwnerOfB;

    /// <summary>Callers this endpoint must never admit.</summary>
    public Actor[] Refused { get; init; } = [];

    /// <summary>
    /// Tenant A's row that this endpoint is asked for while the caller is in tenant B.
    /// Required for <see cref="IsolationKind.ById"/> on a route with one parameter.
    /// </summary>
    public Func<TwoTenantWorld, Guid>? VictimId { get; init; }

    /// <summary>
    /// A value for every route parameter, in the order the template holds them — for the
    /// routes <see cref="VictimId"/> cannot address.
    /// </summary>
    /// <remarks>
    /// Two shapes need it: a route with more than one parameter
    /// (<c>/products/{id}/barcodes/{barcodeId}</c>), and a route whose parameter is not a
    /// <see cref="Guid"/>. Rows that need neither keep using <see cref="VictimId"/>, which
    /// reads better for the majority.
    /// </remarks>
    public Func<TwoTenantWorld, IReadOnlyList<string>>? VictimPath { get; init; }

    public Func<TwoTenantWorld, object?>? Body { get; init; }

    /// <summary>The ids a <see cref="IsolationKind.Collection"/> must return for tenant B.</summary>
    public Func<TwoTenantWorld, IReadOnlyList<Guid>>? Expected { get; init; }

    /// <summary>The ids of tenant A's rows, none of which may appear.</summary>
    public Func<TwoTenantWorld, IReadOnlyList<Guid>>? Forbidden { get; init; }

    /// <summary>
    /// For a write: asserts the cross-tenant attempt changed nothing. A 404 is the promise;
    /// this is the evidence.
    /// </summary>
    public Func<PosApiFactory, TwoTenantWorld, Task>? AssertUntouched { get; init; }

    /// <summary>
    /// Whether a <see cref="IsolationKind.Collection"/> answers with a
    /// <c>{ items, nextCursor, hasMore }</c> envelope rather than a bare JSON array.
    /// </summary>
    /// <remarks>
    /// Stated per row rather than sniffed from the response. "Unwrap <c>items</c> if the
    /// body happens to have it" would keep passing on the day a list endpoint stopped being
    /// paginated, which is precisely the change that ought to be noticed — the manifest
    /// exists to record decisions, not to infer them.
    /// </remarks>
    public bool Paginated { get; init; }

    /// <summary>
    /// Whether this route requires an <c>Idempotency-Key</c> — the 🔒 in docs/API.md.
    /// </summary>
    /// <remarks>
    /// Declared rather than inferred, and then <b>checked against the routing table</b> by
    /// <c>EndpointCoverageTests</c>, so the manifest cannot drift from which endpoints
    /// actually carry the filter.
    /// <para>
    /// It has to be here at all because the filter runs before authorization reaches the
    /// handler and before any route parameter is looked at: without a key, a 🔒 route answers
    /// 400 on the header. A by-id theory expecting 404 would then go red for entirely the
    /// wrong reason — the same structural trap <c>UrlFor</c>'s arity check closed in 2.3,
    /// where a test passed having proven nothing.
    /// </para>
    /// </remarks>
    public bool Idempotent { get; init; }

    /// <summary>Required when <see cref="Kind"/> is <see cref="IsolationKind.Exempt"/>.</summary>
    public string? Exemption { get; init; }

    public string Method => Key[..Key.IndexOf(' ', StringComparison.Ordinal)];

    /// <summary>The route template, exactly as the router holds it.</summary>
    public string Template => Key[(Key.IndexOf(' ', StringComparison.Ordinal) + 1)..];

    /// <summary>How many parameters the route template has for a caller to fill.</summary>
    public int RouteParameterCount => Template.Count(character => character == '{');

    /// <summary>The values this row substitutes into the template, in order.</summary>
    public IReadOnlyList<string> PathValuesFor(TwoTenantWorld world) =>
        VictimPath?.Invoke(world)
        ?? (VictimId is null ? [] : [VictimId(world).ToString()]);

    /// <summary>
    /// The request URL, built by substituting <paramref name="values"/> into the route
    /// template — one value per parameter, in order.
    /// </summary>
    /// <remarks>
    /// Built rather than written out, and this matters more than it looks. The by-id theory
    /// asserts a 404 — and a mistyped URL returns 404 as well, so a hand-written path could
    /// make that test pass while reaching no endpoint at all. Templates here come from
    /// <see cref="IsolationCase.Key"/>, which <see cref="EndpointCoverageTests"/> has already
    /// checked against the routing table, so a route that does not exist cannot be addressed
    /// in the first place.
    /// <para>
    /// <b>The arity check closes the same hole one level down.</b> Substituting only the
    /// first parameter of a two-parameter route leaves a literal <c>{barcodeId:guid}</c> in
    /// the path, which matches no route and answers 404 — indistinguishable from the 404 the
    /// theory is asserting. Throwing here turns that into a failure that says what is wrong.
    /// </para>
    /// </remarks>
    public string UrlFor(IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (values.Count != RouteParameterCount)
        {
            throw new InvalidOperationException(
                $"{Key} has {RouteParameterCount} route parameter(s) but was given {values.Count} "
                + "value(s) to fill them. An unfilled parameter stays in the URL literally, reaches "
                + "no endpoint, and answers the same 404 the by-id theory asserts — so the test "
                + "would pass having proven nothing. Give the row a VictimPath with one value per "
                + "parameter.");
        }

        var url = new StringBuilder("/");
        var remaining = Template.AsSpan();

        for (var next = 0; next < values.Count; next++)
        {
            var open = remaining.IndexOf('{');
            var close = remaining.IndexOf('}');

            url.Append(remaining[..open]).Append(values[next]);
            remaining = remaining[(close + 1)..];
        }

        return url.Append(remaining).ToString();
    }

    /// <summary>
    /// The request URL with <paramref name="id"/> in <i>every</i> route parameter.
    /// </summary>
    /// <remarks>
    /// For the probes that do not care which row they address — the collection theory, where
    /// there is nothing to substitute, and the negative-authorization theory, which uses
    /// <see cref="Guid.Empty"/> precisely because it names a row in no tenant.
    /// </remarks>
    public string UrlFor(Guid id) =>
        UrlFor([.. Enumerable.Repeat(id.ToString(), RouteParameterCount)]);

    public override string ToString() => Key;
}

/// <summary>
/// Every endpoint the application exposes, and how tenant isolation is proven for it.
/// </summary>
/// <remarks>
/// <b>This is the file Phase 2 edits.</b> Mapping a new endpoint without adding a row here
/// fails <see cref="EndpointCoverageTests"/>, which is the whole point of the arrangement:
/// isolation coverage is a build failure when it is missing, not something a reviewer has
/// to notice. It is the same mechanism as
/// <c>RowLevelSecurityTests.Every_tenant_owned_table_is_covered</c> one layer down.
/// <para>
/// An <see cref="IsolationKind.Exempt"/> row must say where the coverage actually lives, so
/// the manifest reads as the phase's audit record rather than as a list of skips.
/// </para>
/// </remarks>
public static class IsolationManifest
{
    public static IReadOnlyList<IsolationCase> Cases { get; } =
    [
        // ---- Collections ------------------------------------------------------------
        new()
        {
            Key = "GET api/v1/registers",
            Kind = IsolationKind.Collection,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            Expected = w => w.B.RegisterIds,
            Forbidden = w => w.A.RegisterIds,
        },
        new()
        {
            Key = "GET api/v1/employees/pin-eligible",
            Kind = IsolationKind.Collection,
            Caller = Actor.DeviceOfB,
            Refused = [Actor.Anonymous, Actor.OwnerOfB, Actor.CashierOfB],
            Expected = w => w.B.PinEligibleIds,
            Forbidden = w => w.A.PinEligibleIds,
        },

        new()
        {
            Key = "GET api/v1/tax-classes",
            Kind = IsolationKind.Collection,
            Paginated = true,

            // A Cashier, because this is CanSell — the register needs a rate to price a
            // line. The first row in this manifest where that actor is the legitimate
            // caller rather than one being turned away.
            Caller = Actor.CashierOfB,

            // OwnerOfB is deliberately absent: CanSell admits every role, so an Owner
            // reading this list is correct, not a leak.
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Expected = w => w.B.TaxClassIds,
            Forbidden = w => w.A.TaxClassIds,
        },

        new()
        {
            Key = "GET api/v1/products",
            Kind = IsolationKind.Collection,
            Paginated = true,
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Expected = w => w.B.ProductIds,
            Forbidden = w => w.A.ProductIds,
        },
        new()
        {
            Key = "GET api/v1/categories",
            Kind = IsolationKind.Collection,
            Paginated = true,
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Expected = w => w.B.CategoryIds,
            Forbidden = w => w.A.CategoryIds,
        },

        new()
        {
            Key = "GET api/v1/stock",
            Kind = IsolationKind.Collection,
            Paginated = true,

            // A Cashier, because this is CanSell: "have we got any more out the back?" is
            // the question the number exists to answer, and it gets asked at the till.
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],

            // Not ProductIds. The carrier bag does not track stock and must not appear, which
            // is the assertion that fails if the endpoint stops filtering.
            Expected = w => w.B.StockedProductIds,
            Forbidden = w => w.A.StockedProductIds,
        },

        // ---- Phase 3.6: sales and shifts ----

        new()
        {
            Key = "GET api/v1/sales",
            Kind = IsolationKind.Collection,
            Paginated = true,

            // A Cashier: CanSell, because looking up the sale you just rang through is
            // something that happens at the till with a customer standing there.
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],

            // All four, voided and refunded included. A history that hid them would still
            // pass a "returns only my tenant's rows" test while being wrong about what a
            // history is for.
            Expected = w => w.B.SaleIds,
            Forbidden = w => w.A.SaleIds,
        },
        new()
        {
            Key = "GET api/v1/sales/{id:guid}",
            Kind = IsolationKind.ById,
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            VictimId = w => w.A.Sales.FirstSaleId,
        },
        new()
        {
            Key = "GET api/v1/sales/by-client-transaction/{clientTransactionId:guid}",
            Kind = IsolationKind.ById,

            // A Cashier, like the two rows above: this is what a till calls after a reload to
            // find out whether the payment it was taking went through.
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],

            // VictimPath rather than VictimId — the route parameter is a client transaction
            // id, not a sale id, and handing it the wrong Guid would 404 for the right reason
            // by accident and prove nothing.
            VictimPath = w => [w.A.Sales.FirstClientTransactionId.ToString()],
        },
        new()
        {
            Key = "GET api/v1/sales/{id:guid}/receipt",
            Kind = IsolationKind.ById,

            // A Cashier: CanSell, because handing a customer their receipt is the last step of
            // serving them and a reprint is asked for at the counter.
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            VictimId = w => w.A.Sales.FirstSaleId,

            // A read, so the 404 is the whole assertion — and it has teeth here: tenant B's own
            // first sale has lines and a tender, so an unscoped handler would answer 200 with
            // somebody else's takings, cashier name and shop address on it.
        },
        new()
        {
            Key = "GET api/v1/stock/discrepancies",
            Kind = IsolationKind.Collection,
            Paginated = true,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            Expected = w => w.B.DiscrepancyIds,
            Forbidden = w => w.A.DiscrepancyIds,
        },
        new()
        {
            Key = "POST api/v1/sales",
            Kind = IsolationKind.Exempt,
            Idempotent = true,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Exemption = "A write with no id in the URL, so neither shape fits. Every id it "
                      + "carries — products, register, shift — is a body field answered 400 "
                      + "rather than 404, identically whether it is unknown or another "
                      + "tenant's, so none of them is an existence oracle. Covered by "
                      + "SaleCommitTests.A_cross_tenant_product_is_refused_and_commits_nothing "
                      + "and A_cross_tenant_shift_is_refused, both of which also assert the "
                      + "victim tenant gained no sale.",
        },
        new()
        {
            Key = "POST api/v1/sales/{id:guid}/void",
            Kind = IsolationKind.ById,
            Idempotent = true,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Sales.FirstSaleId,
            Body = _ => new { reason = "Attempt" },
            AssertUntouched = AssertTenantAsFirstSaleIsStillCompleted,
        },
        new()
        {
            Key = "POST api/v1/sales/{id:guid}/refund",
            Kind = IsolationKind.ById,
            Idempotent = true,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Sales.FirstSaleId,
            Body = w => new
            {
                clientTransactionId = Guid.CreateVersion7(),
                registerId = w.B.FrontCounter.Id,
                shiftId = w.B.Sales.OpenShiftId,
                reason = "Attempt",
            },
            AssertUntouched = AssertNeitherTenantGainedARefund,
        },
        new()
        {
            Key = "POST api/v1/sales/quote",
            Kind = IsolationKind.Exempt,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Exemption = "Writes nothing and has no id in the URL. A cross-tenant productId is "
                      + "answered 400 on the field, the same as POST /sales, because both "
                      + "build their cart through one shared function. Covered by "
                      + "SaleQuoteTests.A_cross_tenant_product_is_refused.",
        },
        new()
        {
            Key = "POST api/v1/shifts",
            Kind = IsolationKind.Exempt,
            Idempotent = true,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Exemption = "A write with no id in the URL. The registerId travels in the body and "
                      + "another tenant's is answered 400 on the field, identically to an "
                      + "unknown one. Covered by "
                      + "ShiftLifecycleTests.A_cross_tenant_register_cannot_have_a_shift_opened_on_it, "
                      + "which also asserts the victim tenant gained no shift.",
        },
        new()
        {
            Key = "POST api/v1/shifts/{id:guid}/close",
            Kind = IsolationKind.ById,
            Idempotent = true,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Sales.OpenShiftId,
            Body = _ => new { countedCash = 500m },
            AssertUntouched = AssertTenantAsOpenShiftIsStillOpen,
        },
        new()
        {
            Key = "POST api/v1/shifts/{id:guid}/cash-movements",
            Kind = IsolationKind.ById,
            Idempotent = true,

            // A Cashier, because CanSell: a drop to the safe is done by whoever is on the
            // till, often the only person in the shop.
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            VictimId = w => w.A.Sales.OpenShiftId,
            Body = _ => new { type = "Drop", amount = -50m, reason = "Attempt" },
            AssertUntouched = AssertNeitherTenantGainedACashMovement,
        },
        new()
        {
            Key = "GET api/v1/shifts/current",
            Kind = IsolationKind.Exempt,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Exemption = "The register is a query parameter rather than a route parameter, so "
                      + "the by-id shape does not fit, and the response is a single object "
                      + "rather than a list. Asking for another tenant's register answers 404 "
                      + "— the same as a register with no open shift. Covered by "
                      + "ShiftLifecycleTests.Another_tenants_register_has_no_current_shift.",
        },

        // ---- By id ------------------------------------------------------------------
        new()
        {
            Key = "GET api/v1/products/{id:guid}",
            Kind = IsolationKind.ById,
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.WaterProductId,

            // A read, so there is nothing to have changed. The 404 is the whole assertion.
        },
        new()
        {
            Key = "PUT api/v1/products/{id:guid}",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.WaterProductId,

            // Valid *in tenant B*: the handler validates the body — including that the tax
            // class exists in the caller's tenant — before it looks the product up. A body
            // naming tenant A's tax class would be answered 400 and the cross-tenant lookup
            // this row exists to test would never run.
            Body = w => new
            {
                sku = "CROSS-TENANT-PUT",
                name = "Attempt",
                unitPrice = 1.0000m,
                taxClassId = w.B.Catalog.StandardTaxClassId,
            },
            AssertUntouched = AssertTenantAsWaterProductIsUnchanged,
        },
        new()
        {
            Key = "POST api/v1/products/{id:guid}/deactivate",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.WaterProductId,
            Body = _ => new { },
            AssertUntouched = AssertTenantAsWaterProductIsUnchanged,
        },
        new()
        {
            Key = "POST api/v1/products/{id:guid}/activate",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.CoffeeProductId,
            Body = _ => new { },
            AssertUntouched = AssertTenantAsCoffeeProductIsUnchanged,
        },
        new()
        {
            Key = "GET api/v1/products/{id:guid}/barcodes",
            Kind = IsolationKind.ById,
            Caller = Actor.CashierOfB,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.WaterProductId,

            // A read. The 404 is the whole assertion — and it is not vacuous here, because
            // tenant B's own water has codes, so an unscoped handler would answer 200.
        },
        new()
        {
            Key = "POST api/v1/products/{id:guid}/barcodes",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.WaterProductId,

            // A code held by nobody in either tenant, so a write that did land is visible as
            // a code that exists rather than as a duplicate-barcode conflict.
            Body = _ => new { code = "9990000000001", isPrimary = false },
            AssertUntouched = AssertNeitherTenantsWaterGainedACode,
        },
        new()
        {
            Key = "DELETE api/v1/products/{id:guid}/barcodes/{barcodeId:guid}",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],

            // Two parameters, so VictimId cannot address it: both of tenant A's ids are
            // needed, and UrlFor refuses to build a URL with one of them left as a literal
            // "{barcodeId:guid}" — which would answer the same 404 this row asserts.
            VictimPath = w =>
            [
                w.A.Catalog.WaterProductId.ToString(),
                w.A.Catalog.WaterBarcodeId.ToString(),
            ],
            AssertUntouched = AssertNeitherTenantsWaterGainedACode,
        },
        new()
        {
            Key = "PUT api/v1/categories/{id:guid}",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.GroceryCategoryId,
            Body = _ => new { name = "Attempt", sortOrder = 99 },
            AssertUntouched = AssertTenantAsGroceryCategoryIsUnchanged,
        },
        new()
        {
            Key = "POST api/v1/categories/{id:guid}/deactivate",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.GroceryCategoryId,
            Body = _ => new { },
            AssertUntouched = AssertTenantAsGroceryCategoryIsUnchanged,
        },
        new()
        {
            Key = "POST api/v1/categories/{id:guid}/activate",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],

            // The world's categories are all active, so "still active" would be true whether
            // this endpoint ran or not. Cheese is the victim here and the assertion reads its
            // name and parent as well, which a stray write would disturb.
            VictimId = w => w.A.Catalog.CheeseCategoryId,
            Body = _ => new { },
            AssertUntouched = AssertTenantAsCheeseCategoryIsUnchanged,
        },
        new()
        {
            Key = "PUT api/v1/tax-classes/{id:guid}",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.StandardTaxClassId,

            // Valid in tenant B, like the set-pin row above: the handler validates the body
            // before it looks the row up, so a malformed one would be answered 400 and the
            // cross-tenant lookup this row exists to test would never run.
            //
            // isDefault is false on purpose, and the gap that leaves is covered elsewhere.
            // This row asserts tenant A's row was not touched. A true here would make the
            // request act on tenant B's *own* default before reaching the 404 — a different
            // failure, and one AssertUntouched cannot see because it only ever reads tenant
            // A. That one is
            // TaxClassCrudTests.A_cross_tenant_promotion_does_not_demote_the_callers_own_default.
            Body = _ => new { name = "Attempt", rate = 0.1000m, isDefault = false },
            AssertUntouched = AssertTenantAsStandardTaxClassIsUnchanged,
        },
        new()
        {
            Key = "GET api/v1/stock/{productId:guid}/movements",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.Catalog.WaterProductId,

            // A read, and one where the 404 has teeth: tenant B's own water has a ledger, so
            // an unscoped handler would answer 200 with somebody else's stock history.
        },
        new()
        {
            Key = "POST api/v1/registers/{id:guid}/enroll",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.FrontCounter.Id,
            Body = _ => new { },
            AssertUntouched = AssertTheFrontCounterStillHoldsItsOriginalToken,
        },
        new()
        {
            Key = "POST api/v1/registers/{id:guid}/revoke",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.FrontCounter.Id,
            Body = _ => new { },
            AssertUntouched = AssertTheFrontCounterStillHoldsItsOriginalToken,
        },
        new()
        {
            Key = "POST api/v1/employees/{id:guid}/set-pin",
            Kind = IsolationKind.ById,
            Caller = Actor.OwnerOfB,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            VictimId = w => w.A.CashierId,

            // A well-formed PIN, and not the world's. The endpoint validates the PIN before
            // it looks the user up, so a malformed one would be answered 400 and the
            // cross-tenant lookup this row exists to test would never run.
            Body = _ => new { pin = "9999" },
            AssertUntouched = AssertTheCashiersPinIsStillTheOneTheWorldSet,
        },

        // ---- Exempt, each saying where the coverage is --------------------------------
        new()
        {
            Key = "GET api/v1/products/by-barcode/{code}",
            Kind = IsolationKind.Exempt,
            Refused = [Actor.Anonymous, Actor.DeviceOfB],
            Exemption = "By-id does not fit, and the reason is the point. The two tenants hold "
                      + "identical catalogs, so tenant A's barcode also exists in tenant B — and "
                      + "B scanning it must get B's own product with a 200, not the 404 the by-id "
                      + "theory asserts. That is a stronger claim than the theory makes, and it is "
                      + "what the identical-catalog fixture exists to enable. Covered by "
                      + "BarcodeLookupTests.A_code_that_exists_in_both_tenants_resolves_to_the_callers_own_product.",
        },
        new()
        {
            Key = "POST api/v1/stock/adjustments",
            Kind = IsolationKind.Exempt,
            Idempotent = true,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            Exemption = "The subject id travels in the body, so there is no URL to attack and "
                      + "neither shape fits. Naming another tenant's product is answered 400 on "
                      + "the field rather than 404 — the same answer an unknown id gets, so it is "
                      + "not an existence oracle. Covered by "
                      + "StockAdjustmentTests.A_cross_tenant_product_id_is_refused_and_moves_no_stock, "
                      + "which also asserts the victim's on-hand is untouched.",
        },
        new()
        {
            Key = "POST api/v1/products",
            Kind = IsolationKind.Exempt,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            Exemption = "A write with no id, so neither shape fits. Covered for tenancy by "
                      + "ProductCrudTests.The_same_sku_is_accepted_in_two_tenants and "
                      + "ProductCrudTests.Another_tenants_tax_class_cannot_be_named, and by "
                      + "ForgedTenancyTests.A_tenant_id_in_the_request_body_is_never_honoured.",
        },
        new()
        {
            Key = "POST api/v1/categories",
            Kind = IsolationKind.Exempt,
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            Exemption = "A write with no id, so neither shape fits. Covered for tenancy by "
                      + "CategoryCrudTests.A_created_category_belongs_to_the_calling_tenant, and "
                      + "for a cross-tenant parent by "
                      + "CategoryCrudTests.Another_tenants_category_cannot_be_named_as_a_parent.",
        },
        new()
        {
            Key = "POST api/v1/tax-classes",
            Kind = IsolationKind.Exempt,

            // Exempt on tenancy, still negatively tested on authorization. Kind and Refused
            // answer different questions, and RefusedCallers() keys off the latter.
            Refused = [Actor.Anonymous, Actor.CashierOfB, Actor.DeviceOfB],
            Exemption = "A write with no id, so neither shape fits. The tenancy question for "
                      + "it is which tenant the row lands in, covered by "
                      + "TaxClassCrudTests.A_created_tax_class_belongs_to_the_calling_tenant "
                      + "and ForgedTenancyTests.A_tenant_id_in_the_request_body_is_never_honoured.",
        },
        new()
        {
            Key = "POST api/v1/registers",
            Kind = IsolationKind.Exempt,
            Exemption = "A write with no id, so neither shape fits. The tenancy question for "
                      + "it is whether a TenantId in the body is honoured: "
                      + "ForgedTenancyTests.A_tenant_id_in_the_request_body_is_never_honoured.",
        },
        new()
        {
            Key = "POST api/v1/auth/login",
            Kind = IsolationKind.Exempt,
            Exemption = "Anonymous, and the endpoint that chooses a tenant rather than acting "
                      + "within one. Covered by LoginTests.A_users_credentials_do_not_work_against_another_tenant "
                      + "and CrossTenantCredentialTests.Identical_credentials_in_both_tenants_resolve_to_the_slug_that_was_named.",
        },
        new()
        {
            Key = "POST api/v1/auth/refresh",
            Kind = IsolationKind.Exempt,
            Exemption = "Anonymous; the tenant comes from the token's own prefix. Covered by "
                      + "RefreshTokenTests.A_refresh_token_from_another_tenant_is_rejected.",
        },
        new()
        {
            Key = "POST api/v1/auth/pin",
            Kind = IsolationKind.Exempt,
            Exemption = "Covered by PinLoginTests.A_device_token_from_another_tenant_admits_nobody. "
                      + "Deliberately kept out of the theories as well: every attempt costs one of "
                      + "the world cashiers' five lockout attempts and one of the till's ten "
                      + "rate-limited requests a minute, so a data-driven loop over it would make "
                      + "unrelated tests fail depending on the order they ran in.",
        },
        new()
        {
            Key = "POST api/v1/auth/override",
            Kind = IsolationKind.Exempt,
            Exemption = "Covered by OverrideGrantTests.A_grant_from_another_tenant_authorises_nothing, "
                      + "which mints against one tenant's till and spends it against the other's sale. "
                      + "Kept out of the theories for the same reason POST /auth/pin is: it verifies a "
                      + "PIN, so every probe spends one of a real user's lockout attempts and one of "
                      + "the till's ten rate-limited requests a minute.",
        },
        new()
        {
            Key = "POST api/v1/auth/logout",
            Kind = IsolationKind.Exempt,
            Exemption = "Answers 204 whatever it is given, on purpose, so its status code proves "
                      + "nothing. Asserted by effect in "
                      + "CrossTenantCredentialTests.Logging_out_cannot_revoke_another_tenants_session.",
        },
        new()
        {
            Key = "GET api/v1/auth/me",
            Kind = IsolationKind.Exempt,
            Exemption = "Self-scoped: it takes no id and returns the caller's own user and tenant. "
                      + "The tenancy question for it is whether the tenant claim can be forged, in "
                      + "ForgedTenancyTests.",
        },
        new()
        {
            // "ANY", not "GET": health checks are mapped without any method metadata, so the
            // router will route every verb to them.
            Key = "ANY health/live",
            Kind = IsolationKind.Exempt,
            Exemption = "Liveness only. Touches no tenant-owned data and reaches no database.",
        },
        new()
        {
            Key = "ANY health/ready",
            Kind = IsolationKind.Exempt,
            Exemption = "Readiness only. Opens a connection but reads no tenant-owned row.",
        },
        new()
        {
            Key = "GET openapi/{documentName}.json",
            Kind = IsolationKind.Exempt,
            Exemption = "The API's own description. Routed in Development and Testing only — "
                      + "never in Production, which is asserted by "
                      + "OpenApiRoutingTests.The_document_is_not_served_outside_development_and_testing. "
                      + "It contains no tenant-owned data of any kind: it describes routes and "
                      + "schemas, and the same bytes are served to every caller. Routed in Testing "
                      + "so IdempotencyDocumentTests and the CI drift check can assert against the "
                      + "real document rather than a rebuilt approximation of it.",
        },
    ];

    public static IReadOnlyDictionary<string, IsolationCase> ByKey { get; } =
        Cases.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static TheoryData<string> KeysOf(IsolationKind kind)
    {
        var data = new TheoryData<string>();

        foreach (var testCase in Cases.Where(c => c.Kind == kind))
        {
            data.Add(testCase.Key);
        }

        return data;
    }

    /// <summary>Every (endpoint, caller-who-must-be-refused) pair, as theory rows.</summary>
    /// <remarks>
    /// Driven off <see cref="IsolationCase.Refused"/> alone, not off <see cref="Kind"/>. The
    /// two are orthogonal questions — <c>Kind</c> says how tenancy is proven, <c>Refused</c>
    /// says who must be turned away — and conflating them left the policy-gated creates
    /// (<c>POST /products</c> and friends) with no negative-authorization coverage at all,
    /// because a write with no id is <c>Exempt</c> on the tenancy question while still being
    /// the endpoint a Cashier most needs to be refused from.
    /// </remarks>
    public static TheoryData<string, Actor> RefusedCallers()
    {
        var data = new TheoryData<string, Actor>();

        foreach (var testCase in Cases.Where(c => c.Refused.Length > 0))
        {
            foreach (var actor in testCase.Refused)
            {
                data.Add(testCase.Key, actor);
            }
        }

        return data;
    }

    /// <summary>
    /// Tenant A's till still answers to the token it was enrolled with — so the attempt to
    /// enroll or revoke it from tenant B neither replaced it nor cleared it.
    /// </summary>
    private static Task AssertTheFrontCounterStillHoldsItsOriginalToken(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var till = await db.Registers.FirstAsync(r => r.Id == world.A.FrontCounter.Id);

            Assert.Equal(OpaqueToken.Hash(world.A.FrontCounter.DeviceToken), till.DeviceTokenHash);
        });

    /// <summary>
    /// Tenant A's Still Water still has its own SKU, name, price, cost and active flag.
    /// </summary>
    /// <remarks>
    /// The cost is asserted too, because a cross-tenant PUT that reached the row would blank
    /// it — the attacker holds <c>CanViewMargins</c> in their own tenant and sends no cost.
    /// </remarks>
    private static Task AssertTenantAsWaterProductIsUnchanged(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.FirstAsync(p => p.Id == world.A.Catalog.WaterProductId);

            Assert.Equal(CatalogFixture.WaterSku, product.Sku);
            Assert.Equal(CatalogFixture.WaterName, product.Name);
            Assert.Equal(1.2000m, product.UnitPrice.ToDecimal());
            Assert.Equal(CatalogFixture.WaterCostPrice, product.CostPrice?.ToDecimal());
            Assert.True(product.IsActive);
        });

    /// <summary>
    /// Still Water carries exactly the two codes the fixture gave it — <b>in both tenants</b>.
    /// </summary>
    /// <remarks>
    /// Tenant A is the obvious half: the 404 is the promise and this is the evidence.
    /// <para>
    /// Tenant B is the half 2.2's falsification pass argued for. A cross-tenant write cannot
    /// actually reach tenant A — the query filter scopes it and row-level security is
    /// underneath — so an assertion that only ever reads A passes for reasons the endpoint
    /// under test had nothing to do with. The failure mode that is genuinely reachable is a
    /// handler writing into the <i>caller's own</i> tenant before it discovers the target is
    /// not there, and only reading B notices that.
    /// </para>
    /// </remarks>
    /// <summary>
    /// The victim's sale is still exactly as it was: completed, with every amount and its
    /// number untouched.
    /// </summary>
    /// <remarks>
    /// A void is the one operation that legitimately updates a completed sale, so a 404 alone
    /// would not prove the attempt did nothing — a handler that voided first and checked
    /// afterwards would answer 404 and still have reversed somebody else's takings.
    /// </remarks>
    private static Task AssertTenantAsFirstSaleIsStillCompleted(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var sale = await db.Sales.FirstAsync(s => s.Id == world.A.Sales.FirstSaleId);

            Assert.Equal(SaleStatus.Completed, sale.Status);
            Assert.Null(sale.VoidedAt);
            Assert.Null(sale.VoidedBy);
            Assert.Null(sale.VoidReason);
            Assert.Equal(1, sale.SaleNumber);
        });

    /// <summary>
    /// The victim's open shift is still open and still uncounted.
    /// </summary>
    /// <remarks>
    /// A 404 alone would not prove the attempt did nothing: closing legitimately updates a
    /// shift, so a handler that closed first and checked afterwards would answer 404 and still
    /// have reconciled somebody else's drawer — and written a variance nobody can explain.
    /// </remarks>
    private static Task AssertTenantAsOpenShiftIsStillOpen(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.FirstAsync(s => s.Id == world.A.Sales.OpenShiftId);

            Assert.Equal(ShiftStatus.Open, shift.Status);
            Assert.Null(shift.ClosedAt);
            Assert.Null(shift.CountedCash);
            Assert.Null(shift.Variance);
        });

    /// <summary>Neither tenant gained a cash movement — the victim's, and the caller's own.</summary>
    private static async Task AssertNeitherTenantGainedACashMovement(
        PosApiFactory factory,
        TwoTenantWorld world)
    {
        await AssertNoCashMovementsAsync(factory, world.A);
        await AssertNoCashMovementsAsync(factory, world.B);
    }

    private static Task AssertNoCashMovementsAsync(PosApiFactory factory, IsolatedTenant tenant) =>
        factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // SalesFixture seeds none, so any at all means the attempt wrote one — in the
            // victim's tenant, or in the caller's own before it noticed.
            Assert.Empty(await db.CashMovements.ToListAsync());
        });

    /// <summary>
    /// Neither tenant gained a refund — the victim's, and the caller's own.
    /// </summary>
    /// <remarks>
    /// Reads <b>both</b>, per the rule 2.2's falsification pass established: the failure mode
    /// that is genuinely reachable is a handler writing into the caller's own tenant before it
    /// discovers the target is not there, and only reading B notices that.
    /// </remarks>
    private static async Task AssertNeitherTenantGainedARefund(
        PosApiFactory factory,
        TwoTenantWorld world)
    {
        await AssertRefundsAreTheSeededOnes(factory, world.A);
        await AssertRefundsAreTheSeededOnes(factory, world.B);
    }

    private static Task AssertRefundsAreTheSeededOnes(PosApiFactory factory, IsolatedTenant tenant) =>
        factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // Exactly the one SalesFixture seeded, and it still points where it did.
            var refund = Assert.Single(await db.Sales.Where(s => s.Type == SaleType.Refund).ToListAsync());

            Assert.Equal(tenant.Sales.RefundSaleId, refund.Id);
            Assert.Equal(tenant.Sales.SecondSaleId, refund.OriginalSaleId);
        });

    private static async Task AssertNeitherTenantsWaterGainedACode(
        PosApiFactory factory,
        TwoTenantWorld world)
    {
        await AssertWatersCodesAreTheSeededOnes(factory, world.A);
        await AssertWatersCodesAreTheSeededOnes(factory, world.B);
    }

    private static Task AssertWatersCodesAreTheSeededOnes(PosApiFactory factory, IsolatedTenant tenant) =>
        factory.AsTenantAsync(tenant.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var codes = await db.Barcodes
                .Where(b => b.ProductId == tenant.Catalog.WaterProductId)
                .Select(b => b.Code)
                .OrderBy(code => code)
                .ToListAsync();

            // Exact equality, so this fails on a code added as well as on one removed. The
            // POST row and the DELETE row share this assertion and they fail in opposite
            // directions.
            Assert.Equal(
                [CatalogFixture.WaterBarcode, CatalogFixture.WaterMultipackBarcode],
                codes);
        });

    /// <summary>Tenant A's Coffee still has its own name, cost and active flag.</summary>
    private static Task AssertTenantAsCoffeeProductIsUnchanged(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var product = await db.Products.FirstAsync(p => p.Id == world.A.Catalog.CoffeeProductId);

            Assert.Equal(CatalogFixture.CoffeeSku, product.Sku);
            Assert.Equal(CatalogFixture.CoffeeCostPrice, product.CostPrice?.ToDecimal());
            Assert.True(product.IsActive);
        });

    /// <summary>
    /// Tenant A's Grocery category still has its own name, sort order and active flag.
    /// </summary>
    private static Task AssertTenantAsGroceryCategoryIsUnchanged(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var category = await db.Categories.FirstAsync(c => c.Id == world.A.Catalog.GroceryCategoryId);

            Assert.Equal(CatalogFixture.GroceryCategoryName, category.Name);
            Assert.Equal(10, category.SortOrder);
            Assert.True(category.IsActive);
            Assert.Null(category.ParentCategoryId);
        });

    /// <summary>
    /// Tenant A's Cheese category still hangs off Grocery, under its own name.
    /// </summary>
    private static Task AssertTenantAsCheeseCategoryIsUnchanged(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var category = await db.Categories.FirstAsync(c => c.Id == world.A.Catalog.CheeseCategoryId);

            Assert.Equal(CatalogFixture.CheeseCategoryName, category.Name);
            Assert.Equal(world.A.Catalog.GroceryCategoryId, category.ParentCategoryId);
            Assert.True(category.IsActive);
        });

    /// <summary>
    /// Tenant A's Standard tax class still has its own name, rate and default flag — so the
    /// PUT from tenant B neither edited it nor demoted it.
    /// </summary>
    /// <remarks>
    /// The 404 is the promise; this is the evidence. A handler that wrote before checking
    /// would still answer 404 and still be wrong, and only this notices.
    /// </remarks>
    private static Task AssertTenantAsStandardTaxClassIsUnchanged(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var taxClass = await db.TaxClasses.FirstAsync(t => t.Id == world.A.Catalog.StandardTaxClassId);

            Assert.Equal(CatalogFixture.StandardTaxClassName, taxClass.Name);
            Assert.Equal(0.2300m, taxClass.Rate);
            Assert.True(taxClass.IsDefault);
        });

    /// <summary>
    /// Tenant A's cashier still has the PIN the world gave them, not the one the request
    /// from tenant B tried to set.
    /// </summary>
    private static Task AssertTheCashiersPinIsStillTheOneTheWorldSet(
        PosApiFactory factory,
        TwoTenantWorld world) =>
        factory.AsTenantAsync(world.A.Id, async services =>
        {
            var users = services.GetRequiredService<UserManager<ApplicationUser>>();
            var cashier = await users.Users.FirstAsync(u => u.Id == world.A.CashierId);

            Assert.NotNull(cashier.PinHash);
            Assert.Equal(
                PasswordVerificationResult.Success,
                users.PasswordHasher.VerifyHashedPassword(
                    cashier, cashier.PinHash, TwoTenantWorld.CashierPin));
        });
}
