using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// <c>POST /stock/adjustments</c> — the only way stock moves by hand.
/// </summary>
/// <remarks>
/// Two things are being defended. The first is the ledger's legibility: a reason is
/// mandatory and a direction that contradicts the reason is refused, because a stock
/// investigation reads these rows and "minus three" answers nothing. The second is that the
/// movement and the on-hand figure move together — asserted through the API here, and
/// through the ledger directly in <c>Pos.Data.Tests</c>.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class StockAdjustmentTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/stock/adjustments";

    [Fact]
    public async Task A_receipt_adds_stock_and_writes_the_movement()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        var body = await AdjustAsync(client, productId, "Receive", 6m, "Delivery from supplier");

        Assert.Equal(6.0000m, body.GetProperty("onHand").GetDecimal());

        var movement = body.GetProperty("movement");

        Assert.Equal("Receive", movement.GetProperty("type").GetString());
        Assert.Equal(6.0000m, movement.GetProperty("quantity").GetDecimal());
        Assert.Equal("Delivery from supplier", movement.GetProperty("reason").GetString());
        Assert.Equal(productId, movement.GetProperty("productId").GetGuid());

        // Server-set, from TimeProvider — the request carries no timestamp at all.
        Assert.True(movement.GetProperty("occurredAt").GetDateTimeOffset() > DateTimeOffset.MinValue);
    }

    [Fact]
    public async Task A_write_off_removes_stock()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AdjustAsync(client, productId, "Receive", 10m, "Delivery");

        var body = await AdjustAsync(client, productId, "Waste", -4m, "Damaged in transit");

        Assert.Equal(6.0000m, body.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task Stock_may_go_negative_rather_than_being_silently_clamped()
    {
        // Deliberate: there is no CHECK on on_hand. A negative figure is evidence something
        // was sold or removed without being received, and clamping it to zero would delete
        // exactly the discrepancy a stocktake needs to find. Phase 3.6 flags oversells for
        // review on the same principle.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        var body = await AdjustAsync(client, productId, "Adjust", -3m, "Found missing at stocktake");

        Assert.Equal(-3.0000m, body.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task A_fractional_quantity_is_kept_to_four_places()
    {
        // 0.350 kg of cheese is an ordinary movement. A quantity column that rounded would
        // lose a gram per weighing, which accumulates into a stocktake nobody can explain.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox, unit: "Kilogram");

        var body = await AdjustAsync(client, productId, "Receive", 2.3456m, "Weighed in");

        Assert.Equal(2.3456m, body.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task The_movement_records_who_made_it()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AdjustAsync(client, productId, "Receive", 1m, "Delivery");

        // Read from the database rather than the response: performedBy is not in the
        // adjustment payload, and the question this answers — "who wrote off the missing
        // six?" — is asked of the ledger months later.
        await factory.AsTenantAsync(sandbox.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var movement = await db.StockMovements.FirstAsync(m => m.ProductId == productId);

            Assert.Equal(sandbox.ManagerId, movement.PerformedBy);
        });
    }

    [Theory]
    [InlineData("Sale")]
    [InlineData("Refund")]
    [InlineData("Recount")]
    public async Task A_type_that_is_not_written_by_hand_is_refused(string type)
    {
        // Sale and Refund belong to the sale that caused them and carry its id. Accepting
        // them here would let someone fabricate sales movements with no sale behind them,
        // and the ledger would stop reconciling with the takings.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        var response = await PostAsync(client, productId, type, 1m, "Attempt");

        await AssertFieldErrorAsync(response, "type");
    }

    [Theory]
    [InlineData("Shrinkage")]
    [InlineData("receive")]        // the converter is case-sensitive, deliberately
    [InlineData("")]
    public async Task A_type_the_enum_does_not_have_is_refused(string type)
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AssertFieldErrorAsync(await PostAsync(client, productId, type, 1m, "Attempt"), "type");
    }

    [Theory]
    [InlineData("Receive", -5)]    // a delivery that removes stock
    [InlineData("Waste", 5)]       // breakage that fills the shelf
    [InlineData("Adjust", 0)]      // a correction that corrects nothing
    [InlineData("Receive", 0)]
    public async Task A_direction_that_contradicts_the_reason_is_refused(string type, int quantity)
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AssertFieldErrorAsync(await PostAsync(client, productId, type, quantity, "Attempt"), "quantity");
    }

    [Fact]
    public async Task A_quantity_the_column_would_round_is_refused()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        // Postgres would round 1.00005 to 1.0001 and report success, so the shop would hold
        // a fraction of a unit nobody entered.
        await AssertFieldErrorAsync(await PostAsync(client, productId, "Receive", 1.00005m, "Attempt"), "quantity");
    }

    [Fact]
    public async Task A_missing_reason_is_refused()
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        var response = await client.PostAsJsonAsync(Route, new
        {
            productId,
            type = "Receive",
            quantity = 5m,
        });

        // Required, not optional. This is the field that makes the ledger worth having.
        await AssertFieldErrorAsync(response, "reason");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task A_blank_reason_is_refused_too(string reason)
    {
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AssertFieldErrorAsync(await PostAsync(client, productId, "Receive", 5m, reason), "reason");
    }

    [Fact]
    public async Task A_product_that_does_not_track_stock_cannot_be_adjusted()
    {
        // A service or an open-price item would otherwise accumulate an on-hand figure that
        // no stocktake will ever reconcile.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox, trackStock: false);

        await AssertFieldErrorAsync(await PostAsync(client, productId, "Receive", 5m, "Attempt"), "productId");
    }

    [Fact]
    public async Task An_unknown_product_is_refused_on_the_field()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await PostAsync(client, Guid.CreateVersion7(), "Receive", 5m, "Attempt");

        // 400 rather than 404: the id is a field of the request, not the resource being
        // addressed, and the answer is the same whether it is unknown or another tenant's.
        await AssertFieldErrorAsync(response, "productId");
    }

    [Fact]
    public async Task A_request_wrong_in_three_places_is_told_about_all_three()
    {
        var (client, _) = await factory.SignedInAsync(RoleNames.Manager);

        var response = await client.PostAsJsonAsync(Route, new { });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        // Accumulated, not short-circuited: a form that surfaces one error per round trip is
        // a form somebody fills in four times.
        Assert.Equal(
            ["productId", "quantity", "reason", "type"],
            errors.EnumerateObject().Select(p => p.Name).Order());
    }

    [Fact]
    public async Task A_cross_tenant_product_id_is_refused_and_moves_no_stock()
    {
        // Named in the isolation manifest as this endpoint's exemption. The subject id is in
        // the body, so there is no URL for the by-id theory to attack.
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(Actor.OwnerOfB, world);

        var response = await client.PostAsJsonAsync(Route, new
        {
            productId = world.A.Catalog.WaterProductId,
            type = "Receive",
            quantity = 500m,
            reason = "Not my product",
        });

        await AssertFieldErrorAsync(response, "productId");

        // The 400 is the promise; this is the evidence. A handler that recorded before
        // checking would answer 400 and still have moved somebody else's stock.
        await factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var stock = await db.StockItems.FirstAsync(s => s.ProductId == world.A.Catalog.WaterProductId);

            Assert.Equal(CatalogFixture.WaterOnHand, stock.OnHand);
            Assert.Empty(await db.StockMovements.Where(m => m.ProductId == world.A.Catalog.WaterProductId)
                .ToListAsync());
        });
    }

    [Fact]
    public async Task A_resubmitted_adjustment_writes_a_second_movement()
    {
        // Pinning a known gap rather than asserting a desired behaviour. docs/API.md marks
        // this endpoint 🔒, but the IdempotencyRecord that would honour an Idempotency-Key is
        // Phase 3.5's, built once for sales, voids, refunds and adjustments together. Until
        // then a double submit really does receive twice — and a movement that is visible in
        // the ledger is a better failure than one silently swallowed.
        //
        // When 3.5 lands, this test is the one that should fail and be rewritten.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Manager);

        var productId = await CreateProductAsync(client, sandbox);

        await AdjustAsync(client, productId, "Receive", 5m, "Delivery");
        var second = await AdjustAsync(client, productId, "Receive", 5m, "Delivery");

        Assert.Equal(10.0000m, second.GetProperty("onHand").GetDecimal());
    }

    [Fact]
    public async Task A_cashier_is_forbidden_and_an_anonymous_caller_unauthorized()
    {
        // 403 versus 401 pinned by hand, as the other catalog resources are: the manifest
        // theory accepts either, and getting them the wrong way round is how a client shows a
        // login prompt to a signed-in cashier who simply lacks a permission.
        var (cashier, sandbox) = await factory.SignedInAsync(RoleNames.Cashier);

        var forbidden = await PostAsync(cashier, sandbox.Catalog.WaterProductId, "Receive", 1m, "Attempt");

        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);

        using var anonymous = factory.CreateClient();

        var unauthorized = await PostAsync(
            anonymous, sandbox.Catalog.WaterProductId, "Receive", 1m, "Attempt");

        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
    }

    private static async Task<JsonElement> AdjustAsync(
        HttpClient client,
        Guid productId,
        string type,
        decimal quantity,
        string reason)
    {
        var response = await PostAsync(client, productId, type, quantity, reason);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(
            $"/api/v1/stock/{productId}/movements",
            response.Headers.Location?.ToString());

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        Guid productId,
        string type,
        decimal quantity,
        string reason) =>
        client.PostAsJsonAsync(Route, new { productId, type, quantity, reason });

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty(field, out _), $"No '{field}' in errors.");
    }

    private static async Task<Guid> CreateProductAsync(
        HttpClient client,
        CatalogSandbox sandbox,
        bool trackStock = true,
        string unit = "Each")
    {
        var response = await client.PostAsJsonAsync("/api/v1/products", new
        {
            sku = $"STK-{Guid.CreateVersion7():N}"[..20],
            name = "Stocked product",
            unitPrice = 2.0000m,
            taxClassId = sandbox.Catalog.StandardTaxClassId,
            trackStock,
            unit,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
