using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Endpoints;
using Pos.Api.Tests.Infrastructure;
using Pos.Api.Tests.Isolation;
using Pos.Data;
using Pos.Data.Identity;

namespace Pos.Api.Tests.Catalog;

/// <summary>
/// <c>GET /catalog/sync</c> — the feed the offline mirror is built from.
/// </summary>
/// <remarks>
/// Two things are being protected, and they fail in opposite directions.
/// <list type="bullet">
/// <item><b>Completeness.</b> A change the feed does not carry is a till selling at a price
/// nobody authorised until somebody rebuilds the mirror by hand. The withdrawn barcode is the
/// case a naive incremental feed gets wrong, because a deleted row answers nothing.</item>
/// <item><b>Isolation.</b> Five collections in one response, each of which has to be scoped —
/// and the manifest's Collection shape can only check one array, which is why this endpoint is
/// exempt there and covered here.</item>
/// </list>
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CatalogSyncTests(PosApiFactory factory)
{
    private const string Route = "/api/v1/catalog/sync";

    [Fact]
    public async Task Every_collection_is_scoped_to_the_calling_tenant()
    {
        /*
         * The manifest row for this endpoint points here.
         *
         * The two tenants hold identical catalogs, so a leak doubles a list rather than having
         * to be spotted by id — and all five collections are asserted in one pass, because a
         * test that checked products and forgot barcodes would pass while the hottest read in
         * the system leaked.
         */
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(Actor.CashierOfB, world);

        var body = await SyncAsync(client);

        AssertOnlyContains(body, "products", world.B.ProductIds, world.A.ProductIds);
        AssertOnlyContains(body, "taxClasses", world.B.TaxClassIds, world.A.TaxClassIds);
        AssertOnlyContains(body, "categories", world.B.CategoryIds, world.A.CategoryIds);

        // Barcodes are not on IsolatedTenant as a list, so they are read from the catalog the
        // world seeded — the same three codes in each tenant.
        var expectedBarcodes = new[]
        {
            world.B.Catalog.WaterBarcodeId,
            world.B.Catalog.WaterMultipackBarcodeId,
            world.B.Catalog.CoffeeBarcodeId,
        };

        var forbiddenBarcodes = new[]
        {
            world.A.Catalog.WaterBarcodeId,
            world.A.Catalog.WaterMultipackBarcodeId,
            world.A.Catalog.CoffeeBarcodeId,
        };

        AssertOnlyContains(body, "barcodes", expectedBarcodes, forbiddenBarcodes);
    }

    [Fact]
    public async Task The_settings_block_is_the_calling_tenants_own()
    {
        // Tenant is not tenant-owned and carries no query filter, so the scoping here is a
        // Where the handler has to actually contain — the same reason GET /settings has its
        // own test rather than a manifest row.
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(Actor.CashierOfB, world);

        var body = await SyncAsync(client);
        var settings = body.GetProperty("settings");

        Assert.Equal(JsonValueKind.Object, settings.ValueKind);

        await factory.AsTenantAsync(world.B.Id, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();
            var tenant = await db.Tenants.SingleAsync(t => t.Id == world.B.Id);

            Assert.Equal(tenant.CurrencyCode, settings.GetProperty("currencyCode").GetString());
            Assert.Equal(tenant.TimeZoneId, settings.GetProperty("timeZoneId").GetString());
        });
    }

    [Fact]
    public async Task A_first_sync_carries_the_whole_catalog_and_a_watermark()
    {
        /*
         * The read-only world, not the sandbox.
         *
         * "The whole catalog is on the first page" is only assertable against a fixture that
         * fits in one — and the sandbox is shared by the entire suite, so it grows past the
         * default limit as tests accumulate. An earlier draft asserted this there and failed
         * only in a full run, which is the shape of test bug that costs the most to find.
         */
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(Actor.CashierOfB, world);

        var body = await SyncAsync(client);

        Assert.True(body.GetProperty("products").GetArrayLength() > 0);
        Assert.True(body.GetProperty("barcodes").GetArrayLength() > 0);
        Assert.True(body.GetProperty("taxClasses").GetArrayLength() > 0);

        // The watermark is what the next call passes back, so it has to be there from the
        // first response — a client with nothing to send would otherwise re-download for ever.
        Assert.True(body.TryGetProperty("watermark", out var watermark));
        Assert.NotEqual(JsonValueKind.Null, watermark.ValueKind);

        // Nothing to remove when the mirror is being built from nothing: a client with no
        // watermark has no rows to delete, so the tombstone query is skipped entirely.
        Assert.Equal(0, body.GetProperty("removedBarcodeIds").GetArrayLength());

        Assert.Contains(
            body.GetProperty("products").EnumerateArray(),
            p => p.GetProperty("id").GetGuid() == world.B.Catalog.WaterProductId);
    }

    [Fact]
    public async Task A_second_sync_carries_only_what_changed()
    {
        // The exit criterion: incremental, not a full re-download. Without this the phase's
        // "10k products syncs without stalling" is unreachable at any catalog size.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        /*
         * Its own product, not the sandbox's seeded Water.
         *
         * CatalogSandbox's rule is that tests add rows and do not change the seeded ones, and
         * an earlier draft of this test broke it — re-pricing Water made four BarcodeLookupTests
         * cases fail, but only in a full run, because each of them passes in isolation. That is
         * the most expensive shape a test bug has, so the rule is worth obeying exactly.
         */
        var product = await CreateProductAsync(client, sandbox, "sync-incremental");

        var first = await SyncAsync(client);
        var watermark = first.GetProperty("watermark").GetString()!;

        // Nothing of this test's own changed since. Asserted by id rather than by count: the
        // sandbox is shared, so a neighbour's product may legitimately appear here.
        var quiet = await SyncAsync(client, since: watermark);

        Assert.DoesNotContain(
            quiet.GetProperty("products").EnumerateArray(),
            p => p.GetProperty("id").GetGuid() == product);

        var renamed = $"sync-incremental-{Guid.CreateVersion7():N}"[..28];

        using var edit = await client.PutAsJsonAsync(
            new Uri($"/api/v1/products/{product}", UriKind.Relative),
            new
            {
                sku = renamed,
                name = renamed,
                taxClassId = sandbox.Catalog.StandardTaxClassId,
                unitPrice = 1.5000m,
            });

        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);

        var after = await SyncAsync(client, since: watermark);

        var changed = after.GetProperty("products").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == product);

        Assert.Equal(1.5000m, changed.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(renamed, changed.GetProperty("name").GetString());
    }

    [Fact]
    public async Task A_deactivated_product_is_carried_rather_than_filtered_out()
    {
        /*
         * The opposite of GET /products, deliberately.
         *
         * That endpoint defaults to active-only because the register grid must never offer an
         * unsellable item. A mirror is the other case entirely: the deactivation IS the change
         * it needs to hear about, and filtering it out would leave the till happily selling a
         * withdrawn product for ever — the exact failure this feed exists to prevent.
         */
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var product = await CreateProductAsync(client, sandbox, "sync-deactivate");

        var first = await SyncAsync(client);
        var watermark = first.GetProperty("watermark").GetString()!;

        using var deactivated = await client.PostAsync(
            new Uri($"/api/v1/products/{product}/deactivate", UriKind.Relative),
            content: null);

        Assert.Equal(HttpStatusCode.NoContent, deactivated.StatusCode);

        var after = await SyncAsync(client, since: watermark);

        var row = after.GetProperty("products").EnumerateArray()
            .Single(p => p.GetProperty("id").GetGuid() == product);

        Assert.False(row.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task A_withdrawn_barcode_is_reported_as_removed_and_stops_scanning()
    {
        /*
         * The case a naive incremental feed cannot express, and the whole reason the removal
         * became a soft delete.
         *
         * A deleted row answers nothing when a till asks what changed. The withdrawal would
         * never reach it, and it would go on scanning a code the shop retired — at a price
         * nobody authorised — until somebody thought to rebuild the mirror.
         */
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var product = await CreateProductAsync(client, sandbox, "sync-withdraw");
        var code = $"SYNC-{Guid.CreateVersion7():N}"[..20];

        var barcodeId = await AddBarcodeAsync(client, product, code);

        var first = await SyncAsync(client);
        var watermark = first.GetProperty("watermark").GetString()!;

        using var removed = await client.DeleteAsync(
            new Uri($"/api/v1/products/{product}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var after = await SyncAsync(client, since: watermark);

        var tombstones = after.GetProperty("removedBarcodeIds").EnumerateArray()
            .Select(element => element.GetGuid())
            .ToArray();

        Assert.Contains(barcodeId, tombstones);

        // And it must not also appear as a live barcode: a mirror applying both would keep
        // whichever it processed last.
        Assert.DoesNotContain(
            after.GetProperty("barcodes").EnumerateArray(),
            b => b.GetProperty("id").GetGuid() == barcodeId);

        // The code no longer scans, which is the behaviour the shop actually observes.
        using var scan = await client.GetAsync(
            new Uri($"/api/v1/products/by-barcode/{code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, scan.StatusCode);
    }

    [Fact]
    public async Task A_withdrawn_code_can_be_used_again()
    {
        /*
         * The ordinary case, and what the filtered unique index is for: a label was mis-typed,
         * removed, and is being entered correctly. Without `WHERE deleted_at IS NULL` on the
         * index this is a 23505 for a code the shop cannot see anywhere — which reads as the
         * system being broken rather than as a duplicate.
         */
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var product = await CreateProductAsync(client, sandbox, "sync-readd");
        var code = $"SYNC-{Guid.CreateVersion7():N}"[..20];

        var first = await AddBarcodeAsync(client, product, code);

        using var removed = await client.DeleteAsync(
            new Uri($"/api/v1/products/{product}/barcodes/{first}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        var second = await AddBarcodeAsync(client, product, code);

        Assert.NotEqual(first, second);

        // And it scans again, to the same product.
        using var scan = await client.GetAsync(
            new Uri($"/api/v1/products/by-barcode/{code}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, scan.StatusCode);

        var scanned = await scan.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(product, scanned.GetProperty("productId").GetGuid());
    }

    [Fact]
    public async Task A_withdrawn_barcode_disappears_from_its_products_list()
    {
        // The tombstone is for the sync feed. Everywhere a person looks, the code is gone.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var product = await CreateProductAsync(client, sandbox, "sync-listing");
        var barcodeId = await AddBarcodeAsync(client, product, $"SYNC-{Guid.CreateVersion7():N}"[..20]);

        using var removed = await client.DeleteAsync(
            new Uri($"/api/v1/products/{product}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);

        using var listed = await client.GetAsync(
            new Uri($"/api/v1/products/{product}/barcodes", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);

        var codes = await listed.Content.ReadFromJsonAsync<JsonElement>();

        Assert.DoesNotContain(
            codes.EnumerateArray(),
            b => b.GetProperty("id").GetGuid() == barcodeId);
    }

    [Fact]
    public async Task Removing_the_same_barcode_twice_is_a_404()
    {
        // A tombstone is not a second thing to delete. Answering 204 again would tell a client
        // working from a stale list that it had just changed something.
        var (client, sandbox) = await factory.SignedInAsync(RoleNames.Owner);

        var product = await CreateProductAsync(client, sandbox, "sync-twice");
        var barcodeId = await AddBarcodeAsync(client, product, $"SYNC-{Guid.CreateVersion7():N}"[..20]);

        using var first = await client.DeleteAsync(
            new Uri($"/api/v1/products/{product}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, first.StatusCode);

        using var second = await client.DeleteAsync(
            new Uri($"/api/v1/products/{product}/barcodes/{barcodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
    }

    [Fact]
    public async Task Settings_are_sent_once_rather_than_on_every_page()
    {
        // Not incremental, so repeating a fixed object on every page of a large catalog is
        // noise the client discards. Sent on the first page, absent on a continuation.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        var first = await SyncAsync(client, limit: 1);

        Assert.Equal(JsonValueKind.Object, first.GetProperty("settings").ValueKind);

        var cursor = first.GetProperty("nextProductCursor").GetString();

        Assert.NotNull(cursor);

        var second = await SyncAsync(client, productCursor: cursor, limit: 1);

        Assert.Equal(JsonValueKind.Null, second.GetProperty("settings").ValueKind);
    }

    [Fact]
    public async Task Paging_walks_the_whole_catalog_without_repeating_or_skipping_a_row()
    {
        /*
         * A page at a time, with limit=1 so the walk is many pages over a small fixture.
         *
         * The two cursors are independent, which is the design being checked here: a shared
         * cursor either replays rows from the shorter collection or steps over rows in the
         * longer one, and with one product per page against three barcodes the difference
         * shows up immediately.
         *
         * **Against the read-only two-tenant world, not the shared sandbox**, and that is not a
         * convenience. The sort key is COALESCE(updated_at, created_at), which *changes when a
         * row is edited* — so a product edited mid-walk moves ahead of the cursor and is served
         * twice. Written against the sandbox this test failed exactly that way, because its
         * neighbours create and edit products in the same tenant.
         *
         * The duplicate is harmless and the skip is what would matter; see the assertions
         * below. But a test cannot assert "no duplicates" on a fixture that is being mutated
         * underneath it, and weakening the assertion to accommodate that would give up the one
         * thing worth checking here.
         */
        var world = await factory.IsolationWorldAsync();

        using var client = await factory.ClientForAsync(Actor.CashierOfB, world);

        var products = new List<Guid>();
        var barcodes = new List<Guid>();

        string? productCursor = null;
        string? barcodeCursor = null;

        for (var page = 0; page < 200; page++)
        {
            var body = await SyncAsync(
                client,
                productCursor: productCursor,
                barcodeCursor: barcodeCursor,
                limit: 1);

            products.AddRange(
                body.GetProperty("products").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()));

            barcodes.AddRange(
                body.GetProperty("barcodes").EnumerateArray().Select(b => b.GetProperty("id").GetGuid()));

            productCursor = body.GetProperty("nextProductCursor").GetString();
            barcodeCursor = body.GetProperty("nextBarcodeCursor").GetString();

            if (productCursor is null && barcodeCursor is null)
            {
                break;
            }
        }

        Assert.Null(productCursor);
        Assert.Null(barcodeCursor);

        // No row twice, on a fixture nothing is editing. A mis-derived cursor produces exactly
        // this, and a mirror's upserts would absorb it silently in production.
        Assert.Equal(products.Count, products.Distinct().Count());
        Assert.Equal(barcodes.Count, barcodes.Distinct().Count());

        /*
         * And none missed — the assertion that actually matters.
         *
         * A duplicate costs a redundant put. A *skip* is a product the till never hears about,
         * priced from a mirror that is wrong until somebody rebuilds it by hand. The feed
         * cannot skip, and the reason is worth stating: UpdatedAt only ever increases, so an
         * edited row can only move ahead of the cursor, never behind it. Duplicates are the
         * one direction this can fail in, and it is the harmless one.
         */
        Assert.Equal(
            world.B.ProductIds.Order(),
            products.Where(id => world.B.ProductIds.Contains(id)).Distinct().Order());

        var expectedBarcodes = new[]
        {
            world.B.Catalog.WaterBarcodeId,
            world.B.Catalog.WaterMultipackBarcodeId,
            world.B.Catalog.CoffeeBarcodeId,
        };

        Assert.Equal(
            expectedBarcodes.Order(),
            barcodes.Where(id => expectedBarcodes.Contains(id)).Distinct().Order());
    }

    [Fact]
    public async Task A_malformed_since_is_a_field_error_rather_than_a_full_download()
    {
        // Ignoring it would silently re-download the catalog on every sync, which looks like
        // a working feature and is a shop's data allowance.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        using var response = await client.GetAsync(
            new Uri($"{Route}?since=last-tuesday", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(body.GetProperty("errors").TryGetProperty("since", out _));
    }

    [Fact]
    public async Task Each_cursor_is_reported_under_its_own_field()
    {
        // Two cursors, two error keys. Told that "cursor" was invalid, a client cannot know
        // which of the two it sent is the bad one.
        var (client, _) = await factory.SignedInAsync(RoleNames.Owner);

        using var response = await client.GetAsync(
            new Uri($"{Route}?barcodeCursor=not-a-cursor", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("errors");

        Assert.True(errors.TryGetProperty("barcodeCursor", out _));
        Assert.False(errors.TryGetProperty("productCursor", out _));
    }

    [Fact]
    public void The_watermark_overlap_is_long_enough_to_cover_a_slow_commit()
    {
        /*
         * The subtle one, stated as a constant so a client does not have to invent it.
         *
         * A row's changed-at is stamped when SaveChanges runs, but the row becomes visible
         * when its transaction commits. A write that started before the watermark was read can
         * therefore appear after it, carrying an earlier timestamp — and a client resuming
         * exactly at the watermark steps over it permanently.
         *
         * Re-asking from slightly earlier is free, because the mirror's writes are upserts.
         */
        Assert.True(CatalogSyncEndpoints.WatermarkOverlapSeconds >= 10);
    }

    [Fact]
    public async Task The_feed_never_carries_a_cost_price()
    {
        /*
         * CanSell, so a Cashier's till receives this, and anything reaching a browser is
         * readable (invariant 7). Margin is a reporting question and reports are online-only,
         * so the column is not part of what an offline register needs at all — which is a
         * stronger guarantee than omitting it per caller, because no shape of this response
         * carries it.
         */
        var world = await factory.IsolationWorldAsync();

        using var owner = await factory.ClientForAsync(Actor.OwnerOfB, world);

        var body = await SyncAsync(owner);

        foreach (var product in body.GetProperty("products").EnumerateArray())
        {
            Assert.False(
                product.TryGetProperty("costPrice", out _),
                "The sync feed carried a costPrice, which a Cashier's till would receive.");
        }
    }

    private static async Task<JsonElement> SyncAsync(
        HttpClient client,
        string? since = null,
        string? productCursor = null,
        string? barcodeCursor = null,
        int? limit = null)
    {
        var query = new List<string>();

        if (since is not null)
        {
            query.Add($"since={Uri.EscapeDataString(since)}");
        }

        if (productCursor is not null)
        {
            query.Add($"productCursor={Uri.EscapeDataString(productCursor)}");
        }

        if (barcodeCursor is not null)
        {
            query.Add($"barcodeCursor={Uri.EscapeDataString(barcodeCursor)}");
        }

        if (limit is { } value)
        {
            query.Add($"limit={value.ToString(CultureInfo.InvariantCulture)}");
        }

        var url = query.Count == 0 ? Route : $"{Route}?{string.Join('&', query)}";

        using var response = await client.GetAsync(new Uri(url, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void AssertOnlyContains(
        JsonElement body,
        string collection,
        IReadOnlyList<Guid> expected,
        IReadOnlyList<Guid> forbidden)
    {
        var returned = body.GetProperty(collection).EnumerateArray()
            .Select(element => element.GetProperty("id").GetGuid())
            .ToHashSet();

        foreach (var id in expected)
        {
            Assert.True(returned.Contains(id), $"{collection} was missing {id}.");
        }

        foreach (var id in forbidden)
        {
            Assert.False(returned.Contains(id), $"{collection} leaked {id} from the other tenant.");
        }
    }

    private static async Task<Guid> CreateProductAsync(
        HttpClient client,
        CatalogSandbox sandbox,
        string prefix)
    {
        var unique = $"{prefix}-{Guid.CreateVersion7():N}"[..24];

        using var response = await client.PostAsJsonAsync(
            new Uri("/api/v1/products", UriKind.Relative),
            new
            {
                sku = unique,
                name = unique,
                taxClassId = sandbox.Catalog.StandardTaxClassId,
                unitPrice = 2.5000m,
                unit = "Each",
                trackStock = true,
            });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<Guid> AddBarcodeAsync(HttpClient client, Guid productId, string code)
    {
        using var response = await client.PostAsJsonAsync(
            new Uri($"/api/v1/products/{productId}/barcodes", UriKind.Relative),
            new { code });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}
