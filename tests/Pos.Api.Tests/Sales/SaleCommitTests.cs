using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Core.Entities;
using Pos.Core.Monetary;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Sales;

/// <summary>
/// <c>POST /sales</c> — the endpoint that must not get this wrong.
/// </summary>
/// <remarks>
/// Everything here is about the transaction being one transaction: the sale, its lines, its
/// tenders, its stock movements, its sale number and its idempotency record land together or
/// not at all.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class SaleCommitTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/sales";

    [Fact]
    public async Task A_sale_records_its_lines_tenders_movements_and_number()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var body = await SellAsync(client, tenant, quantity: 2m, tendered: 5m);

        // Water is 1.2000 at 23%: 2.40 net, 0.55 tax, 2.95 payable, 2.05 change from a fiver.
        Assert.Equal(2.40m, body.GetProperty("subtotal").GetDecimal());
        Assert.Equal(0.55m, body.GetProperty("taxTotal").GetDecimal());
        Assert.Equal(2.95m, body.GetProperty("total").GetDecimal());
        Assert.Equal(2.05m, body.GetProperty("changeGiven").GetDecimal());

        // The first sale of a brand-new tenant. The counter's INSERT arm, which nothing else
        // would ever have created a row for.
        Assert.Equal(1, body.GetProperty("saleNumber").GetInt64());

        var saleId = body.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Single(await db.SaleLines.Where(l => l.SaleId == saleId).ToListAsync());
            Assert.Single(await db.Tenders.Where(t => t.SaleId == saleId).ToListAsync());

            var movement = await db.StockMovements.SingleAsync(m => m.SaleId == saleId);

            // Negative and typed Sale, carrying the sale that caused it — which is what makes
            // the ledger reconcile with the takings.
            Assert.Equal(StockMovementType.Sale, movement.Type);
            Assert.Equal(-2.0000m, movement.Quantity);
            Assert.Null(movement.Reason);

            // Water started at 12.
            var stock = await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.WaterProductId);
            Assert.Equal(10.0000m, stock.OnHand);
        });
    }

    [Fact]
    public async Task The_sale_line_snapshots_the_price_and_rate_it_was_sold_at()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var body = await SellAsync(client, tenant, quantity: 1m, tendered: 5m);
        var saleId = body.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var line = await db.SaleLines.SingleAsync(l => l.SaleId == saleId);

            Assert.Equal((Money)1.2000m, line.UnitPrice);
            Assert.Equal(0.2300m, line.TaxRate);
            Assert.Equal(CatalogFixture.WaterName, line.Description);
        });
    }

    [Fact]
    public async Task A_client_sent_total_is_ignored()
    {
        // There is nowhere to put one: CreateSaleRequest has no total field at all. A price a
        // client can send is a price a customer can edit, so this asserts the shape rather
        // than a filter — an extra JSON field is dropped by binding, and the server's own
        // arithmetic is what comes back.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 5m } },
            total = 0.01m,
            taxTotal = 0m,
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(1.48m, body.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_quote_and_a_sale_of_the_same_cart_agree_on_every_amount()
    {
        // The exit criterion, and it holds by construction rather than by coincidence: both
        // routes build their Cart through one function and price it with one call.
        var (client, tenant) = await factory.TradingTenantAsync();

        var cart = new
        {
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 3m },
                new { productId = tenant.Catalog.CoffeeProductId, quantity = 1m },
            },
        };

        using var quoted = await client.PostAsJsonAsync($"{Route}/quote", cart);
        Assert.Equal(HttpStatusCode.OK, quoted.StatusCode);

        var quote = await quoted.Content.ReadFromJsonAsync<JsonElement>();

        using var sold = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            cart.lines,
            tenders = new[] { new { method = "Cash", amount = 50m } },
        });

        Assert.Equal(HttpStatusCode.Created, sold.StatusCode);

        var sale = await sold.Content.ReadFromJsonAsync<JsonElement>();

        foreach (var field in new[] { "subtotal", "discountTotal", "taxTotal", "roundingAdjustment", "total" })
        {
            Assert.Equal(quote.GetProperty(field).GetDecimal(), sale.GetProperty(field).GetDecimal());
        }
    }

    [Fact]
    public async Task Oversell_takes_stock_negative_rather_than_refusing_the_sale()
    {
        // The customer is standing at the counter holding the item. Refusing is the wrong
        // behaviour and is how a POS gets thrown out; the sale completes and the discrepancy
        // is flagged for someone to investigate.
        var (client, tenant) = await factory.TradingTenantAsync();

        // Coffee's on-hand is 3.
        var body = await SellAsync(client, tenant, quantity: 5m, tendered: 50m, tenant.Catalog.CoffeeProductId);

        var saleId = body.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var stock = await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.CoffeeProductId);
            Assert.Equal(-2.0000m, stock.OnHand);

            var discrepancy = await db.StockDiscrepancies.SingleAsync(d => d.SaleId == saleId);

            Assert.Equal(5m, discrepancy.QuantityRequested);
            Assert.Equal(-2m, discrepancy.OnHandAfter);
        });
    }

    [Fact]
    public async Task Selling_the_last_unit_exactly_is_not_a_discrepancy()
    {
        // The boundary that decides whether the report is worth reading. Three of three lands
        // on zero and is an ordinary sale; flagging it would bury the real oversells.
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, quantity: 3m, tendered: 50m, tenant.Catalog.CoffeeProductId);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Equal(0.0000m,
                (await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.CoffeeProductId)).OnHand);

            Assert.Empty(await db.StockDiscrepancies.ToListAsync());
        });
    }

    [Fact]
    public async Task Discrepancies_are_listed_for_a_manager()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await SellAsync(client, tenant, quantity: 5m, tendered: 50m, tenant.Catalog.CoffeeProductId);

        using var response = await client.GetAsync(new Uri("/api/v1/stock/discrepancies", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var items = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");

        Assert.Equal(1, items.GetArrayLength());
        Assert.Equal(tenant.Catalog.CoffeeProductId, items[0].GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task A_product_that_does_not_track_stock_moves_none()
    {
        // The carrier bag. Selling it must not create a movement, or every shop would
        // accumulate a meaningless negative on-hand for its packaging.
        var (client, tenant) = await factory.TradingTenantAsync();

        var body = await SellAsync(client, tenant, quantity: 1m, tendered: 5m, tenant.Catalog.BagProductId);
        var saleId = body.GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.StockMovements.Where(m => m.SaleId == saleId).ToListAsync());
        });
    }

    [Fact]
    public async Task An_under_tender_is_refused_and_commits_nothing()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await PostAsync(client, tenant, quantity: 2m, tendered: 1m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://pos.example/errors/under-tender", body.GetProperty("type").GetString());

        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task A_sale_against_a_closed_shift_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var shift = await db.Shifts.SingleAsync(s => s.Id == tenant.ShiftId);

            shift.Status = ShiftStatus.Closed;
            await db.SaveChangesAsync();
        });

        using var response = await PostAsync(client, tenant, quantity: 1m, tendered: 5m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://pos.example/errors/shift-closed", body.GetProperty("type").GetString());

        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task A_register_that_does_not_own_the_shift_is_refused()
    {
        // Two ids for one fact is a client bug, and a sale attributed to the wrong drawer
        // makes a Z-report reconcile the wrong till.
        var (client, tenant) = await factory.TradingTenantAsync();

        Guid otherRegisterId = default;

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var register = new Register { Name = "Back Counter", IsActive = true };

            db.Registers.Add(register);
            await db.SaveChangesAsync();

            otherRegisterId = register.Id;
        });

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = otherRegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task A_cross_tenant_product_is_refused_and_commits_nothing()
    {
        // The row POST /sales' isolation exemption names. 400 on the field, identical to an
        // unknown id, so it is not an existence oracle.
        var (client, tenant) = await factory.TradingTenantAsync();
        var world = await factory.IsolationWorldAsync();

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = world.A.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        await AssertFieldErrorAsync(response, "lines[0].productId");
        await AssertNothingCommittedAsync(tenant);

        // And the victim tenant is untouched — the failure mode a test that only read the
        // caller's own tenant would miss.
        await factory.AsTenantAsync(world.A.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var stock = await db.StockItems.SingleAsync(s => s.ProductId == world.A.Catalog.WaterProductId);

            Assert.Equal(CatalogFixture.WaterOnHand, stock.OnHand);
        });
    }

    [Fact]
    public async Task A_cross_tenant_shift_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();
        var world = await factory.IsolationWorldAsync();

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = world.A.Sales.OpenShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        await AssertFieldErrorAsync(response, "shiftId");
        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task A_cashier_cannot_send_a_discount_or_a_price_override()
    {
        // Refused rather than silently dropped, unlike an unreadable costPrice: this changes
        // what the customer pays, so ignoring it would take the shop's money instead of theirs.
        var (_, tenant) = await factory.TradingTenantAsync();

        await factory.CreateUserAsync(
            tenant.TenantId, "cashier@trading.test", TradingTenant.Password, RoleNames.Cashier, "Robin Vale");

        using var cashier = factory.CreateClient();
        cashier.WithBearer((await cashier.LoginAsync(
            tenant.Slug, "cashier@trading.test", TradingTenant.Password)).AccessToken);

        using var discounted = await cashier.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },
            cartDiscountAmount = 0.50m,
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, discounted.StatusCode);

        using var overridden = await cashier.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 1m, unitPriceOverride = 0.01m },
            },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.Forbidden, overridden.StatusCode);

        await AssertNothingCommittedAsync(tenant);
    }

    [Fact]
    public async Task An_owner_may_override_a_price_and_the_line_records_who_did()
    {
        // The positive control. Without it the test above would pass on an endpoint that
        // refused everybody.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = tenant.Catalog.WaterProductId, quantity = 1m, unitPriceOverride = 0.50m },
            },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var saleId = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var line = await db.SaleLines.SingleAsync(l => l.SaleId == saleId);

            Assert.True(line.IsPriceOverridden);

            // Until Phase 7.2's audit log exists, this column is the whole record of it.
            Assert.Equal(tenant.OwnerId, line.OverriddenBy);
        });
    }

    [Fact]
    public async Task An_unaccepted_tender_method_is_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 1m } },

            // A declared TenderMethod the MVP does not implement. Accepting it would put money
            // in the takings that no processor ever saw.
            tenders = new[] { new { method = "Card", amount = 5m } },
        });

        await AssertFieldErrorAsync(response, "tenders[0].method");
    }

    [Fact]
    public async Task An_empty_cart_and_a_zero_quantity_are_refused()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var empty = await client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = Array.Empty<object>(),
            tenders = new[] { new { method = "Cash", amount = 5m } },
        });

        await AssertFieldErrorAsync(empty, "lines");

        using var zero = await PostAsync(client, tenant, quantity: 0m, tendered: 5m);

        await AssertFieldErrorAsync(zero, "lines[0].quantity");
    }

    [Fact]
    public async Task A_deactivated_product_cannot_be_sold()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        using var deactivated = await client.PostAsJsonAsync(
            $"/api/v1/products/{tenant.Catalog.WaterProductId}/deactivate", new { });

        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        using var response = await PostAsync(client, tenant, quantity: 1m, tendered: 5m);

        // It still scans — the register says "not for sale" rather than "unknown item" — but
        // selling it is a different question with a different answer.
        await AssertFieldErrorAsync(response, "lines[0].productId");
    }

    [Fact]
    public async Task Parallel_sales_produce_unique_sale_numbers()
    {
        // The counter's whole purpose. MAX()+1 read outside the transaction would hand the
        // same number to two of these, and ux_sale_tenant_sale_number would turn that into a
        // 500 at the till.
        var (client, tenant) = await factory.TradingTenantAsync();

        const int Parallel = 10;

        var clients = new List<HttpClient>();

        for (var index = 0; index < Parallel; index++)
        {
            var extra = factory.CreateClient();
            extra.WithBearer((await extra.LoginAsync(
                tenant.Slug, TradingTenant.OwnerEmail, TradingTenant.Password)).AccessToken);

            clients.Add(extra);
        }

        var responses = await Task.WhenAll(clients.Select(c => PostAsync(c, tenant, 1m, 5m)));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.Created, r.StatusCode));

        var numbers = new List<long>();

        foreach (var response in responses)
        {
            numbers.Add((await response.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("saleNumber").GetInt64());

            response.Dispose();
        }

        foreach (var extra in clients)
        {
            extra.Dispose();
        }

        // Distinct, and gapless: 1..10 with nothing burned by a rollback.
        Assert.Equal(Parallel, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, Parallel).Select(n => (long)n), numbers.Order());

        client.Dispose();
    }

    [Fact]
    public async Task Concurrent_last_unit_sales_both_succeed_or_one_is_told_to_retry()
    {
        // Two registers reaching for the same last unit. StockItem's xmin makes them collide
        // loudly rather than one silently overwriting the other's on-hand — the loser is told
        // 409 and never given a wrong total.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var second = factory.CreateClient();
        second.WithBearer((await second.LoginAsync(
            tenant.Slug, TradingTenant.OwnerEmail, TradingTenant.Password)).AccessToken);

        var responses = await Task.WhenAll(
            PostAsync(client, tenant, 3m, 50m, tenant.Catalog.CoffeeProductId),
            PostAsync(second, tenant, 3m, 50m, tenant.Catalog.CoffeeProductId));

        var codes = responses.Select(r => r.StatusCode).ToArray();

        // Never a 500, and never a silent success that lost a movement.
        Assert.DoesNotContain(HttpStatusCode.InternalServerError, codes);
        Assert.All(codes, c => Assert.True(
            c is HttpStatusCode.Created or HttpStatusCode.Conflict,
            $"Unexpected status {c}."));

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            // The invariant, whichever way the race went: the cached total still equals its
            // opening balance plus its ledger. A lost update would leave the two disagreeing.
            //
            // The opening balance has to be added because CatalogFixture seeds stock rows
            // without the receipt that would explain them — it predates the ledger. Asserting
            // OnHand == SUM(movements) outright would fail against the fixture rather than
            // against the code, which is the kind of green-for-the-wrong-reason this suite
            // works hard to avoid in the other direction.
            var stock = await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.CoffeeProductId);
            var ledger = await db.StockMovements
                .Where(m => m.ProductId == tenant.Catalog.CoffeeProductId)
                .SumAsync(m => m.Quantity);

            Assert.Equal(CatalogFixture.CoffeeOnHand + ledger, stock.OnHand);

            // Exactly one of them moved stock, or both did. What must not happen is two
            // movements against an on-hand that only fell once.
            var movements = await db.StockMovements
                .Where(m => m.ProductId == tenant.Catalog.CoffeeProductId)
                .CountAsync();

            Assert.Equal(codes.Count(c => c == HttpStatusCode.Created), movements);
        });

        foreach (var response in responses)
        {
            response.Dispose();
        }
    }

    [Fact]
    public async Task A_replayed_sale_returns_the_original_and_writes_nothing_more()
    {
        var (client, tenant) = await factory.TradingTenantAsync();

        var key = Guid.CreateVersion7();
        var clientTransactionId = Guid.CreateVersion7();

        var body = new
        {
            clientTransactionId,
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[] { new { productId = tenant.Catalog.WaterProductId, quantity = 2m } },
            tenders = new[] { new { method = "Cash", amount = 5m } },
        };

        using var first = await client.PostIdempotentAsync(Route, body, key);
        using var second = await client.PostIdempotentAsync(Route, body, key);

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);

        // A cashier double-tapping on a slow connection must not charge twice. This is the
        // assertion the whole of 3.5 exists for, at the endpoint it exists for.
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Single(await db.Sales.ToListAsync());
            Assert.Single(await db.StockMovements.ToListAsync());

            Assert.Equal(10.0000m,
                (await db.StockItems.SingleAsync(s => s.ProductId == tenant.Catalog.WaterProductId)).OnHand);
        });
    }

    [Fact]
    public async Task A_forced_failure_mid_transaction_leaves_no_sale_no_lines_and_no_movements()
    {
        // The atomicity claim, forced rather than hoped for. The sale number is taken and the
        // sale row is written before the tender validation that fails here, so a transaction
        // that was not really one would leave a sale behind with no tender and a decremented
        // stock figure.
        //
        // Under-tender is the lever because it is raised by the endpoint AFTER pricing and
        // before the writer opens its transaction; the cross-tenant product test covers the
        // same ground from the other side. What matters is that nothing partial survives.
        var (client, tenant) = await factory.TradingTenantAsync();

        using var response = await PostAsync(client, tenant, quantity: 2m, tendered: 0.01m);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Empty(await db.SaleLines.ToListAsync());
            Assert.Empty(await db.Tenders.ToListAsync());
            Assert.Empty(await db.StockMovements.ToListAsync());

            // And the counter was never advanced, so the next real sale is number 1.
            Assert.Empty(await db.SaleSequences.ToListAsync());
        });
    }

    private static Task<HttpResponseMessage> PostAsync(
        HttpClient client,
        TradingTenant tenant,
        decimal quantity,
        decimal tendered,
        Guid? productId = null) =>
        client.PostIdempotentAsync(Route, new
        {
            clientTransactionId = Guid.CreateVersion7(),
            registerId = tenant.RegisterId,
            shiftId = tenant.ShiftId,
            lines = new[]
            {
                new { productId = productId ?? tenant.Catalog.WaterProductId, quantity },
            },
            tenders = new[] { new { method = "Cash", amount = tendered } },
        });

    private static async Task<JsonElement> SellAsync(
        HttpClient client,
        TradingTenant tenant,
        decimal quantity,
        decimal tendered,
        Guid? productId = null)
    {
        using var response = await PostAsync(client, tenant, quantity, tendered, productId);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task AssertNothingCommittedAsync(TradingTenant tenant) =>
        await factory.AsTenantAsync(tenant.TenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            Assert.Empty(await db.Sales.ToListAsync());
            Assert.Empty(await db.StockMovements.ToListAsync());
        });

    private static async Task AssertFieldErrorAsync(HttpResponseMessage response, string field)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty(field, out _), $"No '{field}' in errors.");
    }
}
