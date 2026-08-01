using Npgsql;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Catalog;

/// <summary>
/// That the product search index is the kind of index <c>ILIKE '%q%'</c> can use.
/// </summary>
/// <remarks>
/// <b>Why not an <c>EXPLAIN</c> test.</b> The obvious assertion — run the query and check
/// the plan names this index — is a statement about planner economics, not about the index.
/// Measured by hand against a seeded catalog: at 3,000 rows Postgres prefers to scan the
/// tenant through any narrow btree and filter the <c>ILIKE</c>, even with
/// <c>enable_seqscan</c> off, because a GIN bitmap scan has a high startup cost. Only at
/// around 50,000 rows does it switch — and there it is unambiguous, taking
/// <c>ix_product_tenant_name_trgm</c> with <i>both</i> predicates as <c>Index Cond</c>,
/// which is the multicolumn GIN working exactly as designed. Neither number belongs in a
/// test: one would assert something false, and the other would insert fifty thousand rows
/// on every CI run to re-derive a fact about Postgres's cost model.
/// <para>
/// What is worth asserting is the shape, because that is what a well-meaning fix would
/// destroy. Someone facing a red build can make the index "present, on the right table,
/// with the right columns, in the right order" by recreating it as a plain btree — every
/// assertion in <c>DatabaseSchemaTests</c> would go green again and <c>ILIKE</c> would
/// sequential-scan for the rest of the product's life. These tests are what notices.
/// </para>
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class CatalogSearchIndexTests(PostgresFixture postgres)
{
    [Fact]
    public async Task Both_search_extensions_are_installed_by_the_migration()
    {
        var installed = await ScalarListAsync("SELECT extname FROM pg_extension ORDER BY extname;");

        // Declared on the model with HasPostgresExtension rather than as raw SQL, so they
        // reach AppDbContextModelSnapshot too. Read back from the database here because the
        // snapshot saying so is not the same statement as the database having them.
        Assert.Contains("pg_trgm", installed, StringComparer.Ordinal);

        // Not optional and not merely a companion: btree_gin is what supplies GIN an
        // operator class for the uuid column, and without it tenant_id cannot be in this
        // index at all. A GIN index on `name` alone would then fail
        // DatabaseSchemaTests.Every_index_on_a_tenant_table_leads_with_the_tenant.
        Assert.Contains("btree_gin", installed, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_product_search_index_is_a_trigram_gin_index_led_by_the_tenant()
    {
        var columns = await ReadOperatorClassesAsync("ix_product_tenant_name_trgm");

        Assert.Equal(2, columns.Count);

        // GIN, not btree. A btree cannot answer a leading-wildcard match at any width.
        Assert.All(columns, c => Assert.Equal("gin", c.Method));

        // uuid_ops on tenant_id, which only exists inside GIN because btree_gin is
        // installed — this is the assertion that fails if someone removes the extension
        // thinking it is unused.
        Assert.Equal(("tenant_id", "uuid_ops"), (columns[0].Column, columns[0].OperatorClass));

        // The one that makes the index worth having.
        Assert.Equal(("name", "gin_trgm_ops"), (columns[1].Column, columns[1].OperatorClass));
    }

    [Fact]
    public async Task The_ordering_index_beside_it_is_still_a_btree()
    {
        // The GIN index did not replace ix_product_tenant_name, it joined it. GIN cannot
        // serve ORDER BY, and the cursor pages by (name, id) — so deleting the btree as a
        // duplicate would turn every page of the catalog into a sort of the whole tenant.
        var columns = await ReadOperatorClassesAsync("ix_product_tenant_name");

        Assert.Equal(2, columns.Count);
        Assert.All(columns, c => Assert.Equal("btree", c.Method));
    }

    private sealed record IndexedColumn(string Column, string OperatorClass, string Method);

    /// <summary>
    /// The per-column operator classes of one index, in index order, as Postgres holds them.
    /// </summary>
    /// <remarks>
    /// <c>indclass</c> is unnested alongside <c>indkey</c> and joined on the shared ordinal:
    /// the two arrays are parallel, and reading either alone would say nothing about which
    /// operator class applies to which column.
    /// </remarks>
    private async Task<List<IndexedColumn>> ReadOperatorClassesAsync(string indexName)
    {
        var results = new List<IndexedColumn>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.attname, oc.opcname, am.amname
            FROM pg_index ix
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = i.relnamespace AND n.nspname = 'public'
            JOIN pg_am am ON am.oid = i.relam
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON TRUE
            JOIN LATERAL unnest(ix.indclass) WITH ORDINALITY AS c(class, ord) ON c.ord = k.ord
            JOIN pg_attribute a ON a.attrelid = ix.indrelid AND a.attnum = k.attnum
            JOIN pg_opclass oc ON oc.oid = c.class
            WHERE i.relname = @index
            ORDER BY k.ord;
            """;
        command.Parameters.AddWithValue("index", indexName);

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new IndexedColumn(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        // A misspelled index name returns nothing, and every Assert below would then be
        // asserting over an empty list. Fail here instead, saying which name found nothing.
        Assert.True(results.Count > 0, $"No index named {indexName} exists.");

        return results;
    }

    private async Task<List<string>> ScalarListAsync(string sql)
    {
        var results = new List<string>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(reader.GetString(0));
        }

        return results;
    }
}
