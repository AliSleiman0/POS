using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class StockItemConfiguration : IEntityTypeConfiguration<StockItem>
{
    public void Configure(EntityTypeBuilder<StockItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("stock_item");
        builder.HasKey(s => s.Id);

        // Tenant-scoped, for the reason spelled out in BarcodeConfiguration.
        builder.HasOne<Product>()
            .WithMany()
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .HasForeignKey(s => new { s.TenantId, s.ProductId })
            .OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("fk_stock_item_product");

        // One stock row per product, and the index the foreign key uses — the only place
        // in the catalog where the uniqueness the data model asks for and the index the
        // relationship needs are the same index.
        builder.HasIndex(s => new { s.TenantId, s.ProductId })
            .IsUnique()
            .HasDatabaseName("ux_stock_item_tenant_product");

        // Maps onto Postgres' xmin system column: no column of our own, and it cannot
        // drift from the row because the database bumps it on every update. This is the
        // mechanism Phase 3.6 relies on to catch two registers selling the last unit.
        //
        // All three parts matter. IsRowVersion() makes it a concurrency token generated on
        // add and update — which is what the Npgsql convention matches on, together with
        // the property being a uint. The column name is stated because the snake_case
        // convention would otherwise rename it to row_version, at which point it is an
        // ordinary column that nothing maintains.
        builder.Property(s => s.RowVersion)
            .IsRowVersion()
            .HasColumnName("xmin")
            .HasColumnType("xid");
    }
}
