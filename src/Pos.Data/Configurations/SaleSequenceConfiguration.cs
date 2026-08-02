using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class SaleSequenceConfiguration : IEntityTypeConfiguration<SaleSequence>
{
    public void Configure(EntityTypeBuilder<SaleSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("sale_sequence");
        builder.HasKey(s => s.Id);

        // On (tenant_id) ALONE, and that is not a slip. The sale writer takes the next number
        // with a single
        //     INSERT ... ON CONFLICT (tenant_id) DO UPDATE SET last_number = last_number + 1
        //     RETURNING last_number
        // and Postgres resolves an ON CONFLICT target against a unique constraint on exactly
        // those columns. The usual (tenant_id, id) composite would not match, and the
        // statement fails outright — loudly, at least, rather than silently issuing
        // duplicates.
        //
        // One row per tenant is also the truth being expressed: a second counter row for a
        // tenant is two sequences racing to hand out the same numbers.
        builder.HasIndex(s => s.TenantId)
            .IsUnique()
            .HasDatabaseName("ux_sale_sequence_tenant");
    }
}
