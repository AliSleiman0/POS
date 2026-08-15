using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;

namespace Pos.Data.Configurations;

internal sealed class OrderSequenceConfiguration : IEntityTypeConfiguration<OrderSequence>
{
    public void Configure(EntityTypeBuilder<OrderSequence> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customer_order_sequence");
        builder.HasKey(s => s.Id);

        // On (tenant_id) ALONE, matching ux_sale_sequence_tenant and for the same reason: the
        // writer takes the next number with a single
        //     INSERT ... ON CONFLICT (tenant_id) DO UPDATE SET last_number = last_number + 1
        //     RETURNING last_number
        // and Postgres resolves an ON CONFLICT target against a unique constraint on exactly
        // those columns. A (tenant_id, id) composite would not match and the statement fails
        // outright — loudly, rather than silently issuing duplicate order numbers.
        builder.HasIndex(s => s.TenantId)
            .IsUnique()
            .HasDatabaseName("ux_customer_order_sequence_tenant");
    }
}
