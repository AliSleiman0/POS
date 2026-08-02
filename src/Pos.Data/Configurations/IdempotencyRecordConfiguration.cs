using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pos.Core.Entities;
using Pos.Core.Idempotency;

namespace Pos.Data.Configurations;

internal sealed class IdempotencyRecordConfiguration : IEntityTypeConfiguration<IdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<IdempotencyRecord> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("idempotency_record");
        builder.HasKey(r => r.Id);

        builder.HasBoundedText(r => r.Endpoint, "endpoint", IdempotencyRecord.EndpointMaxLength);
        builder.HasBoundedText(r => r.RequestHash, "request_hash", RequestFingerprint.Length);

        // No length bound. A response body is whatever the endpoint returned, and a limit here
        // would turn "this sale had a lot of lines" into a failed write after the money moved.
        builder.Property(r => r.ResponseBody).HasColumnType("text");

        // On (tenant_id, key) and deliberately NOT including the endpoint. A key reused on a
        // different endpoint is the same client bug as one reused with a different body, and
        // it earns the same 409 rather than quietly succeeding twice. The endpoint is inside
        // request_hash instead, which is what makes it a mismatch.
        //
        // This index IS the mechanism, not a safety net behind a lookup: two concurrent
        // retries both miss the read and both insert, and the loser blocks here until the
        // winner commits — at which point the winner's row is visible to re-read and replay.
        builder.HasIndex(r => new { r.TenantId, r.Key })
            .IsUnique()
            .HasDatabaseName("ux_idempotency_record_tenant_key");
    }
}
