using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;
using Pos.Core.Tenancy;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Schema;

/// <summary>One column of one index, as Postgres actually holds it.</summary>
internal sealed record IndexColumn(string Table, string Index, bool IsUnique, string Column, int Ordinal);

/// <summary>
/// What the migration actually built, read back out of the Postgres catalog.
/// </summary>
/// <remarks>
/// <c>TenantModelTests</c> asserts the model; this asserts the database. They are not the
/// same statement — a model is only a schema once a migration says so, and EF will happily
/// hold a mapping that no migration ever emitted. Every assertion here would survive the
/// model being deleted.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class DatabaseSchemaTests(PostgresFixture postgres)
{
    /// <summary>
    /// The indexes docs/DATA-MODEL.md#key-indexes names for the catalog, with the columns
    /// in the order the index holds them.
    /// </summary>
    public static TheoryData<string, string, bool, string[]> KeyIndexes => new()
    {
        { "barcode", "ux_barcode_tenant_code", true, ["tenant_id", "code"] },
        { "product", "ux_product_tenant_sku", true, ["tenant_id", "sku"] },
        { "product", "ix_product_tenant_name", false, ["tenant_id", "name"] },
        { "stock_item", "ux_stock_item_tenant_product", true, ["tenant_id", "product_id"] },

        // Phase 2.2. The btree above and the GIN below share a column list and do entirely
        // different jobs: one serves ORDER BY (name, id) for the cursor, the other serves
        // ILIKE '%q%'. Neither can do the other's.
        { "product", "ix_product_tenant_name_trgm", false, ["tenant_id", "name"] },
        { "category", "ix_category_tenant_name", false, ["tenant_id", "name"] },
        { "tax_class", "ix_tax_class_tenant_name", false, ["tenant_id", "name"] },

        // Phase 2.4. Not unique — a product legitimately has many movements, and two of them
        // can share an instant. The column order is the whole value: it serves "this
        // product's ledger" and "oldest first" with one index, which is exactly what the
        // paged endpoint and the on-hand rebuild both ask for.
        {
            "stock_movement",
            "ix_stock_movement_tenant_product_occurred",
            false,
            ["tenant_id", "product_id", "occurred_at"]
        },

        // Phase 3.3. The two unique indexes on sale are the load-bearing ones.
        //
        // ux_sale_tenant_client_transaction_id IS the idempotency guarantee — not a check in
        // the write path, which two concurrent submissions of one cart would both pass before
        // either inserted. If this stopped being unique, a double-tapped sale would charge a
        // customer twice and every test of the replay path would still be green, because they
        // exercise the sequential case.
        {
            "sale",
            "ux_sale_tenant_client_transaction_id",
            true,
            ["tenant_id", "client_transaction_id"]
        },

        // The human reference an auditor quotes. Unique per tenant; the counter row is what
        // keeps it gapless, and this index is what proves the counter did its job.
        { "sale", "ux_sale_tenant_sale_number", true, ["tenant_id", "sale_number"] },

        { "sale", "ix_sale_tenant_completed_at", false, ["tenant_id", "completed_at"] },
        { "sale", "ix_sale_tenant_shift", false, ["tenant_id", "shift_id"] },
        { "sale", "ix_sale_tenant_original", false, ["tenant_id", "original_sale_id"] },

        // Two lines of one sale sharing a line number is a genuine ambiguity: it is what a
        // receipt prints and what a partial refund names.
        {
            "sale_line",
            "ux_sale_line_tenant_sale_line_number",
            true,
            ["tenant_id", "sale_id", "line_number"]
        },
        { "sale_line", "ix_sale_line_tenant_sale", false, ["tenant_id", "sale_id"] },

        // "How much of this line has already been refunded?", summed on every refund. Without
        // OriginalSaleLineId the only alternative is matching on product id, which breaks the
        // moment one sale has the same product on two lines.
        {
            "sale_line",
            "ix_sale_line_tenant_original",
            false,
            ["tenant_id", "original_sale_line_id"]
        },

        { "tender", "ix_tender_tenant_sale", false, ["tenant_id", "sale_id"] },

        // On (tenant_id) alone, deliberately. The sale writer's
        // INSERT ... ON CONFLICT (tenant_id) DO UPDATE resolves its conflict target against a
        // unique constraint on exactly these columns; the usual (tenant_id, id) composite
        // would not match and the statement would fail.
        { "sale_sequence", "ux_sale_sequence_tenant", true, ["tenant_id"] },

        // Filtered on status = 'Open', so a register may have many closed shifts and at most
        // one open one. A plain unique index here would make a second shift impossible ever.
        { "shift", "ux_shift_tenant_register_open", true, ["tenant_id", "register_id"] },
        { "shift", "ix_shift_tenant_opened_at", false, ["tenant_id", "opened_at"] },

        {
            "stock_discrepancy",
            "ix_stock_discrepancy_tenant_detected",
            false,
            ["tenant_id", "detected_at"]
        },

        // "Which movements did this sale write?" — what a void reads in order to compensate
        // them. Added in 3.3 alongside stock_movement.sale_id finally getting a foreign key.
        { "stock_movement", "ix_stock_movement_tenant_sale", false, ["tenant_id", "sale_id"] },
    };

    [Theory]
    [MemberData(nameof(KeyIndexes))]
    public async Task The_indexes_the_data_model_names_are_present(
        string table,
        string index,
        bool unique,
        string[] columns)
    {
        var found = (await ReadIndexColumnsAsync())
            .Where(c => string.Equals(c.Index, index, StringComparison.Ordinal))
            .OrderBy(c => c.Ordinal)
            .ToArray();

        Assert.True(found.Length > 0, $"Index {index} does not exist on {table}.");
        Assert.Equal(table, found[0].Table);

        // Column order, not just membership: (code, tenant_id) would be the same set and a
        // useless index, because every query filters on the tenant first.
        Assert.Equal(columns, found.Select(c => c.Column).ToArray());

        // Uniqueness asserted separately, because a ux_-prefixed index that is not unique
        // is a name that lies — and the thing it is supposed to prevent, two products with
        // one SKU, would then be storable.
        Assert.Equal(unique, found[0].IsUnique);
    }

    [Fact]
    public async Task Every_index_on_a_tenant_table_leads_with_the_tenant()
    {
        var tables = await DomainTableNamesAsync();
        var indexes = await ReadIndexColumnsAsync();

        var offenders = indexes
            .Where(c => tables.Contains(c.Table) && c.Ordinal == 1)
            .Where(c => !string.Equals(c.Column, "tenant_id", StringComparison.Ordinal))
            .Select(c => $"{c.Table}.{c.Index} (leads with {c.Column})")
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Indexes in the database not leading with tenant_id: " + string.Join(", ", offenders));

        // The table list comes from the model, so it grows with the schema. If it ever came
        // back empty this test would pass having checked nothing.
        Assert.Contains("barcode", tables, StringComparer.Ordinal);
        Assert.Contains("product", tables, StringComparer.Ordinal);
    }

    [Fact]
    public async Task Every_money_and_quantity_column_is_numeric_19_4()
    {
        var offenders = new List<string>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT table_name, column_name, numeric_precision, numeric_scale
            FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type = 'numeric'
            ORDER BY table_name, column_name;
            """;

        var checkedColumns = 0;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var table = reader.GetString(0);
            var column = reader.GetString(1);
            var precision = reader.GetInt32(2);
            var scale = reader.GetInt32(3);

            checkedColumns++;

            // One deliberate exception, stated here rather than skipped: a tax rate is a
            // fraction and is narrowed on purpose. Anything else at a different width is a
            // property that escaped the model-wide convention.
            var (expectedPrecision, expectedScale) = (table, column) switch
            {
                ("tax_class", "rate") => (6, 4),
                _ => (19, 4),
            };

            if (precision != expectedPrecision || scale != expectedScale)
            {
                offenders.Add($"{table}.{column} is numeric({precision},{scale})");
            }
        }

        // Scanning every table rather than the five new ones is the point: this is what
        // catches a Phase 3 column mapped at the provider default of numeric(18,2), which
        // silently truncates the 3rd and 4th decimals of a unit price.
        Assert.True(offenders.Count == 0, "Numeric columns at the wrong width: " + string.Join(", ", offenders));
        Assert.True(checkedColumns >= 5, $"Only {checkedColumns} numeric columns were examined.");
    }

    [Fact]
    public async Task The_catalog_tables_carry_their_check_constraints()
    {
        var names = new List<string>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT conname FROM pg_constraint
            WHERE contype = 'c'
              AND conrelid IN ('product'::regclass, 'barcode'::regclass, 'tax_class'::regclass,
                               'category'::regclass, 'stock_movement'::regclass)
            ORDER BY conname;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        // The length bounds and the enum's allowed values are only real if the migration
        // emitted them — a constraint that lives in the model and not the database stops a
        // C# caller and nothing else.
        Assert.Contains("ck_product_sku_length", names, StringComparer.Ordinal);
        Assert.Contains("ck_product_name_length", names, StringComparer.Ordinal);
        Assert.Contains("ck_product_unit_allowed", names, StringComparer.Ordinal);
        Assert.Contains("ck_barcode_code_length", names, StringComparer.Ordinal);
        Assert.Contains("ck_tax_class_name_length", names, StringComparer.Ordinal);
        Assert.Contains("ck_tax_class_rate_range", names, StringComparer.Ordinal);
        Assert.Contains("ck_category_name_length", names, StringComparer.Ordinal);

        // The ledger's type column is text, so this constraint is the only thing standing
        // between an import and a movement type the application has no case for — in a table
        // that is append-only and therefore keeps whatever it is given forever.
        Assert.Contains("ck_stock_movement_type_allowed", names, StringComparer.Ordinal);
        Assert.Contains("ck_stock_movement_reason_length", names, StringComparer.Ordinal);
    }

    [Fact]
    public async Task The_tax_rate_column_is_narrower_in_the_database_too()
    {
        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT numeric_precision, numeric_scale FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = 'tax_class' AND column_name = 'rate';
            """;

        await using var reader = await command.ExecuteReaderAsync();

        Assert.True(await reader.ReadAsync(), "tax_class.rate does not exist.");
        Assert.Equal(6, reader.GetInt32(0));
        Assert.Equal(4, reader.GetInt32(1));
    }

    /// <summary>The tables of every tenant-owned entity we configure, taken from the model.</summary>
    private async Task<HashSet<string>> DomainTableNamesAsync()
    {
        await using var scoped = ScopedDbContext.WithoutTenant(postgres.ConnectionString);

        return scoped.Db.Model.GetEntityTypes()
            .Where(e => typeof(ITenantOwned).IsAssignableFrom(e.ClrType) && e.BaseType is null)
            .Where(e => e.ClrType.Namespace == "Pos.Core.Entities")
            .Select(e => e.GetTableName())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<List<IndexColumn>> ReadIndexColumnsAsync()
    {
        var results = new List<IndexColumn>();

        await using var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();

        // Primary keys excluded: they are on `id` alone by design, and the tenant reaches
        // them through the alternate key instead.
        command.CommandText = """
            SELECT t.relname, i.relname, ix.indisunique, a.attname, k.ord
            FROM pg_class t
            JOIN pg_namespace n ON n.oid = t.relnamespace AND n.nspname = 'public'
            JOIN pg_index ix ON ix.indrelid = t.oid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON TRUE
            JOIN pg_attribute a ON a.attrelid = t.oid AND a.attnum = k.attnum
            WHERE t.relkind = 'r' AND NOT ix.indisprimary
            ORDER BY t.relname, i.relname, k.ord;
            """;

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new IndexColumn(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetBoolean(2),
                reader.GetString(3),
                (int)reader.GetInt64(4)));
        }

        return results;
    }
}
