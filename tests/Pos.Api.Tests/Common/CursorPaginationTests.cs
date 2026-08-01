using System.Buffers.Text;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Api.Common;
using Pos.Api.Tests.Infrastructure;
using Pos.Core.Entities;
using Pos.Data;
using Pos.Core.Monetary;

namespace Pos.Api.Tests.Common;

/// <summary>
/// The pagination helper on its own, before any endpoint depends on it.
/// </summary>
/// <remarks>
/// Two things here are worth more than the rest. The <c>ToQueryString</c> test is the gate:
/// the keyset predicate is built by rewriting an expression tree, and if Npgsql does not
/// recognise the result as a row-value comparison the failure is either a runtime
/// translation exception or — worse — a silent client-side evaluation that pages by loading
/// the whole table. And the wrong-sort test is what stands between a cursor from one
/// endpoint and a cast failure at another.
/// </remarks>
[Collection(PosApiCollection.Name)]
public sealed class CursorPaginationTests(PosApiFactory factory)
{
    private const string Sort = "product:name";

    [Fact]
    public void A_cursor_round_trips_a_string_key()
    {
        var id = Guid.CreateVersion7();

        var cursor = PageCursor.Encode(Sort, "COFFEE 250G", id);

        Assert.True(PageCursor.TryDecode<string>(cursor, Sort, out var position));
        Assert.Equal("COFFEE 250G", position!.Key);
        Assert.Equal(id, position.Id);
    }

    [Fact]
    public void A_cursor_round_trips_a_guid_key()
    {
        // The shape a future id-ordered list uses — one code path, no special case for
        // "the key is the id".
        var key = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();

        var cursor = PageCursor.Encode("sale:id", key, id);

        Assert.True(PageCursor.TryDecode<Guid>(cursor, "sale:id", out var position));
        Assert.Equal(key, position!.Key);
        Assert.Equal(id, position.Id);
    }

    [Fact]
    public void A_cursor_is_url_safe()
    {
        // Base64url, so it survives a query string untouched. Plain base64 would emit
        // '+', '/' and '=', each of which a client would have to escape and the server
        // unescape — and one missed round trip is a cursor that silently stops decoding.
        var cursor = PageCursor.Encode(Sort, "Crème Fraîche & Co / 50%", Guid.CreateVersion7());

        Assert.DoesNotContain("+", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("/", cursor, StringComparison.Ordinal);
        Assert.DoesNotContain("=", cursor, StringComparison.Ordinal);
        Assert.Equal(Uri.EscapeDataString(cursor), cursor);
    }

    [Theory]
    [InlineData("not base64url at all!!")]
    [InlineData("////")]
    public void A_cursor_that_is_not_base64url_is_refused(string cursor)
    {
        Assert.False(PageCursor.TryDecode<string>(cursor, Sort, out _));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("""{"v":1,"s":"product:name","i":"0199a3c1-0000-7000-8000-000000000000"}""")]     // no k
    [InlineData("""{"v":2,"s":"product:name","k":"A","i":"0199a3c1-0000-7000-8000-000000000000"}""")]
    [InlineData("""{"v":1,"s":"product:name","k":null,"i":"0199a3c1-0000-7000-8000-000000000000"}""")]
    public void A_cursor_whose_payload_is_wrong_is_refused(string json)
    {
        var cursor = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(json));

        Assert.False(PageCursor.TryDecode<string>(cursor, Sort, out _));
    }

    [Fact]
    public void A_cursor_minted_for_a_different_sort_order_is_refused()
    {
        // The case this exists for: /tax-classes hands out a cursor, a client sends it to
        // /products. Without the sort token the keyset predicate would compare product.name
        // against a tax class's key — a cast failure at the database, surfacing as a 500.
        var cursor = PageCursor.Encode("tax-class:name", "Standard", Guid.CreateVersion7());

        Assert.False(PageCursor.TryDecode<string>(cursor, Sort, out _));
    }

    [Fact]
    public void A_cursor_whose_key_is_the_wrong_type_is_refused()
    {
        // Reachable only by hand: this endpoint's key is a string and the cursor holds a
        // Guid. It must be a refusal rather than the NotSupportedException the serialiser
        // would otherwise throw out of a query-string parse.
        var cursor = PageCursor.Encode(Sort, "not-a-guid", Guid.CreateVersion7());

        Assert.False(PageCursor.TryDecode<Guid>(cursor, Sort, out _));
    }

    [Fact]
    public void An_oversized_cursor_is_refused_on_length_before_it_is_decoded()
    {
        // Deliberately a *valid* cursor — right version, right sort token, decodable key —
        // that is only too long. It is refused anyway, which is what shows the length guard
        // runs first. Without that ordering a multi-megabyte query parameter would be
        // base64-decoded and JSON-parsed before being rejected on its contents.
        var cursor = PageCursor.Encode(Sort, new string('x', 2000), Guid.CreateVersion7());

        Assert.True(cursor.Length > 512);
        Assert.False(PageCursor.TryDecode<string>(cursor, Sort, out _));
    }

    [Fact]
    public void An_absent_cursor_means_the_first_page_rather_than_an_error()
    {
        Assert.True(PageQuery.TryRead<string>(cursor: null, limit: null, Sort, out var request, out var errors));

        Assert.Empty(errors);
        Assert.Null(request.After);
        Assert.Equal(PageQuery.DefaultLimit, request.Limit);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_blank_cursor_also_means_the_first_page(string cursor)
    {
        Assert.True(PageQuery.TryRead<string>(cursor, limit: null, Sort, out var request, out _));

        Assert.Null(request.After);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(PageQuery.MaxLimit + 1)]
    public void A_limit_outside_the_range_is_refused_rather_than_clamped(int limit)
    {
        Assert.False(PageQuery.TryRead<string>(cursor: null, limit, Sort, out _, out var errors));

        // Refused, because clamping hides a client bug: a report that quietly stops at 200
        // rows is discovered weeks later as "the numbers don't add up".
        Assert.Equal(["limit"], errors.Keys);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(PageQuery.MaxLimit)]
    public void The_limits_at_the_edge_of_the_range_are_accepted(int limit)
    {
        Assert.True(PageQuery.TryRead<string>(cursor: null, limit, Sort, out var request, out _));

        Assert.Equal(limit, request.Limit);
    }

    [Fact]
    public void A_bad_limit_and_a_bad_cursor_are_both_reported()
    {
        // Validation accumulates rather than short-circuiting. Fixing one field only to be
        // told about the next is the failure mode of hand-rolled validation.
        Assert.False(PageQuery.TryRead<string>("!!not-a-cursor!!", limit: 0, Sort, out _, out var errors));

        Assert.Equal(["limit", "cursor"], errors.Keys.Order().Reverse());
    }

    [Fact]
    public async Task The_keyset_predicate_translates_to_a_postgres_row_value_comparison()
    {
        // The gate for the whole helper. Guid has no > operator in C# and EF has no
        // Guid.CompareTo translator, so the expanded "k > @k OR (k = @k AND id > @id)" form
        // cannot be written at all — the design rests entirely on Npgsql rendering
        // EF.Functions.GreaterThan(ITuple, ITuple) as a row value. If that stopped matching,
        // the alternatives are a translation exception or a silent client-side evaluation
        // that pages by loading the entire table, and only this test tells them apart.
        var tenant = await factory.CreateTenantAsync("keyset-sql", "Corner Shop");

        string sql = string.Empty;

        await factory.AsTenantAsync(tenant.Id, services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var request = new PageRequest<string>(
                Sort,
                Limit: 50,
                After: new PagePosition<string>("Coffee 250g", Guid.CreateVersion7()));

            sql = CursorPaging
                .PageQueryFor(db.Products.AsNoTracking(), p => p.Name, p => p.Id, request)
                .ToQueryString();

            return Task.CompletedTask;
        });

        var flattened = sql.Replace("\r", string.Empty, StringComparison.Ordinal);

        // "(p.name, p.id) > (@__after_Key_0, @__after_Id_1)" — the row value, and both sides
        // parameterised rather than inlined as literals, which is what keeps Postgres's plan
        // cache useful across pages.
        Assert.Matches(@"\(\s*p\.name\s*,\s*p\.id\s*\)\s*>\s*\(\s*@", flattened);

        // The id tiebreaker in the ORDER BY, asserted here rather than left to
        // Paging_through_returns_every_row_exactly_once — see the note on that test for why
        // it cannot pin this. Without it the last row of a page is arbitrary among equal
        // names, so the cursor resumes from an arbitrary one and the rest are never served.
        Assert.Matches(@"ORDER BY\s+p\.name\s*,\s*p\.id", flattened);

        // The extra row that answers hasMore without a COUNT(*).
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Paging_through_returns_every_row_exactly_once()
    {
        // The SQL test above proves the predicate translates. This proves it translates to
        // the *right* predicate — a comparison written the wrong way round produces
        // perfectly valid SQL that returns the first page forever.
        var tenant = await factory.CreateTenantAsync("keyset-walk", "Corner Shop");

        // Five products share one name, so the walk has to cross a run of rows that are
        // equal on the sort key and can only be separated by the id.
        //
        // Note what this test does NOT prove. Deleting `.ThenBy(entity => entity.Id)` leaves
        // it green — established by trying it. The keyset predicate is a row value over
        // (name, id) either way, and Postgres happens to return the equal-named rows in
        // physical order, which here coincides with id order because Guid.CreateVersion7 is
        // monotonic and the rows were inserted in that order. The ordering is still required
        // for correctness — without it the last row of a page is arbitrary among the equal
        // ones — but it takes a structural assertion to pin it, and that lives in
        // The_keyset_predicate_translates_to_a_postgres_row_value_comparison.
        var names = new[] { "Apple", "Apple", "Apple", "Apple", "Apple", "Banana", "Cherry" };
        var seeded = await SeedProductsAsync(tenant.Id, names);

        var seen = new List<Guid>();
        PagePosition<string>? after = null;

        for (var page = 0; page < 10; page++)
        {
            var request = new PageRequest<string>(Sort, Limit: 2, after);
            CursorPage<Guid> result = default!;

            await factory.AsTenantAsync(tenant.Id, async services =>
            {
                var db = services.GetRequiredService<AppDbContext>();

                result = await db.Products
                    .AsNoTracking()
                    .ToPageAsync(p => p.Name, p => p.Id, request, CancellationToken.None);
            });

            seen.AddRange(result.Items);

            if (!result.HasMore)
            {
                Assert.Null(result.NextCursor);
                break;
            }

            Assert.NotNull(result.NextCursor);
            Assert.True(PageCursor.TryDecode(result.NextCursor, Sort, out after));
        }

        // Every id exactly once: no duplicates from a stalled cursor, none skipped.
        Assert.Equal(seeded.Order(), seen.Order());
        Assert.Equal(seeded.Count, seen.Distinct().Count());
    }

    [Fact]
    public async Task A_row_inserted_behind_the_cursor_does_not_disturb_the_next_page()
    {
        // The exit criterion: "stable across concurrent inserts". Stability here means the
        // page is defined by position rather than by offset — insert anything before the
        // cursor and the next page is unaffected. Offset paging would shift every subsequent
        // page by one and serve a row twice.
        var tenant = await factory.CreateTenantAsync("keyset-insert", "Corner Shop");
        await SeedProductsAsync(tenant.Id, ["Damson", "Elderberry", "Fig"]);

        var first = await ReadPageAsync(tenant.Id, new PageRequest<string>(Sort, Limit: 1, After: null));

        Assert.True(first.HasMore);
        Assert.True(PageCursor.TryDecode(first.NextCursor!, Sort, out PagePosition<string>? after));

        // Sorts before the cursor's position, so it belongs to a page already served.
        await SeedProductsAsync(tenant.Id, ["Apricot"]);

        var second = await ReadPageAsync(tenant.Id, new PageRequest<string>(Sort, Limit: 10, after));

        // Elderberry and Fig, and emphatically not Apricot: the page is "what follows
        // Damson", and inserting behind that cannot change the answer.
        var names = await NamesOfAsync(tenant.Id, second.Items);

        Assert.Equal(["Elderberry", "Fig"], names);
    }

    private async Task<CursorPage<Guid>> ReadPageAsync(Guid tenantId, PageRequest<string> request)
    {
        CursorPage<Guid> result = default!;

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            result = await db.Products
                .AsNoTracking()
                .ToPageAsync(p => p.Name, p => p.Id, request, CancellationToken.None);
        });

        return result;
    }

    private async Task<List<string>> NamesOfAsync(Guid tenantId, IReadOnlyList<Guid> ids)
    {
        var names = new List<string>();

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            foreach (var id in ids)
            {
                names.Add(await db.Products.Where(p => p.Id == id).Select(p => p.Name).SingleAsync());
            }
        });

        return names;
    }

    /// <summary>
    /// Writes one product per name, returning their ids.
    /// </summary>
    /// <remarks>
    /// Two SaveChanges, principals before dependents: there are no navigation properties, so
    /// EF does no foreign-key fixup and the tax class's Id is Guid.Empty until the tenant
    /// interceptor stamps it during the first save.
    /// </remarks>
    private async Task<List<Guid>> SeedProductsAsync(Guid tenantId, string[] names)
    {
        var ids = new List<Guid>();

        await factory.AsTenantAsync(tenantId, async services =>
        {
            var db = services.GetRequiredService<AppDbContext>();

            var taxClass = await db.TaxClasses.FirstOrDefaultAsync();

            if (taxClass is null)
            {
                taxClass = new TaxClass { Name = "Standard", Rate = 0.2300m };
                db.TaxClasses.Add(taxClass);
                await db.SaveChangesAsync();
            }

            foreach (var name in names)
            {
                var product = new Product
                {
                    Sku = Product.NormalizeSku($"{name}-{Guid.CreateVersion7():N}")!,
                    Name = name,
                    TaxClassId = taxClass.Id,
                    UnitPrice = (Money)1.0000m,
                };

                db.Products.Add(product);
                await db.SaveChangesAsync();
                ids.Add(product.Id);
            }
        });

        return ids;
    }
}
