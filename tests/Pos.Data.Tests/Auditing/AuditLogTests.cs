using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pos.Core.Auditing;
using Pos.Core.Entities;
using Pos.Data.Identity;
using Pos.Data.Tests.Infrastructure;

namespace Pos.Data.Tests.Auditing;

/// <summary>
/// The guarantee the audit log exists to make: an entry commits with the action it describes,
/// or not at all.
/// </summary>
/// <remarks>
/// Everything here goes through the real DI registration rather than constructing
/// <c>AuditLog</c> by hand, because the mechanism <i>is</i> the registration — staging works
/// only because the writers and the log resolve the same scoped <c>AppDbContext</c>. A
/// hand-built log with its own context would pass every test below and be useless in the
/// application.
/// </remarks>
[Collection(PostgresCollection.Name)]
public sealed class AuditLogTests(PostgresFixture postgres)
{
    [Fact]
    public async Task A_staged_entry_commits_with_the_callers_save()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var productId = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId, actorId);
        var audit = scoped.Services.GetRequiredService<IAuditLog>();

        audit.Record(
            AuditAction.StockAdjusted,
            nameof(Product),
            productId,
            before: new Dictionary<string, string?> { ["onHand"] = "10" },
            after: new Dictionary<string, string?> { ["onHand"] = "7" });

        // Nothing is written yet. Record() stages; the caller's save is what commits, which
        // is exactly what makes the entry share a transaction with the work it describes.
        Assert.Empty(await ReadAsync(tenantId));

        await scoped.Db.SaveChangesAsync();

        var entry = Assert.Single(await ReadAsync(tenantId));

        Assert.Equal(AuditAction.StockAdjusted, entry.Action);
        Assert.Equal(nameof(Product), entry.EntityType);
        Assert.Equal(productId, entry.EntityId);
        Assert.Equal(actorId, entry.ActorId);
        Assert.Null(entry.RegisterId);

        // Read back as a dictionary rather than compared as a string, because jsonb is not a
        // string: Postgres parses it on write and re-renders it on read, so what comes back is
        // `{"onHand": "10"}` with a space that was never sent. That normalisation is the
        // reason IdempotencyRecord.ResponseBody is deliberately text — it has to replay
        // byte-identically — and the reason it is fine here.
        Assert.Equal(new Dictionary<string, string?> { ["onHand"] = "10" }, Payload(entry.Before));
        Assert.Equal(new Dictionary<string, string?> { ["onHand"] = "7" }, Payload(entry.After));
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_no_entry()
    {
        var tenantId = Guid.NewGuid();

        await using (var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId))
        {
            var audit = scoped.Services.GetRequiredService<IAuditLog>();

            // Wrapped, because EnableRetryOnFailure refuses a user-initiated transaction
            // outright — the same wall SaleWriter and TaxClassEndpoints hit. Doing it here
            // means the test rolls back the way the application would, not the way a
            // hand-built context would let it.
            var strategy = scoped.Db.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await scoped.Db.Database.BeginTransactionAsync();

                scoped.Db.Registers.Add(new Register { Name = "Doomed Till" });
                audit.Record(AuditAction.DeviceEnrolled, nameof(Register), Guid.NewGuid());

                await scoped.Db.SaveChangesAsync();

                // The half of the contract that is easy to get backwards. An entry that
                // survives its own rollback is worse than a missing one: it reports an action
                // that never happened, and the only evidence against it is the absence of
                // everything else.
                await transaction.RollbackAsync();
            });
        }

        Assert.Empty(await ReadAsync(tenantId));
    }

    [Fact]
    public async Task A_replayed_block_writes_one_entry_rather_than_two()
    {
        var tenantId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);
        var audit = scoped.Services.GetRequiredService<IAuditLog>();

        var payload = new Dictionary<string, string?> { ["reason"] = "Wrong item" };

        // What a transient failure does to SaleWriter: the execution strategy replays the
        // block, the change tracker still holds what the failed attempt added, and the
        // onCommitting callback fires a second time. Nothing downstream would catch the
        // duplicate — an audit entry has no unique index to violate — so the log itself has
        // to notice.
        audit.Record(AuditAction.SaleVoided, nameof(Sale), saleId, after: payload);
        audit.Record(AuditAction.SaleVoided, nameof(Sale), saleId, after: payload);

        await scoped.Db.SaveChangesAsync();

        Assert.Single(await ReadAsync(tenantId));
    }

    [Fact]
    public async Task Two_genuinely_different_actions_on_one_row_are_both_kept()
    {
        var tenantId = Guid.NewGuid();
        var saleId = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);
        var audit = scoped.Services.GetRequiredService<IAuditLog>();

        // The other direction, and the reason the replay guard compares the payload rather
        // than just the action and the id: one sale legitimately carries a discount on two
        // different lines, and a guard that collapsed them would quietly lose half the trail.
        audit.Record(
            AuditAction.DiscountApplied,
            nameof(SaleLine),
            saleId,
            after: new Dictionary<string, string?> { ["amount"] = "1.00" });
        audit.Record(
            AuditAction.DiscountApplied,
            nameof(SaleLine),
            saleId,
            after: new Dictionary<string, string?> { ["amount"] = "2.50" });

        await scoped.Db.SaveChangesAsync();

        Assert.Equal(2, (await ReadAsync(tenantId)).Count);
    }

    [Fact]
    public async Task An_entry_from_a_till_records_which_till()
    {
        var tenantId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        Guid registerId;

        await using (var setup = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId))
        {
            var register = new Register { Name = "Front Counter" };
            setup.Db.Registers.Add(register);
            await setup.Db.SaveChangesAsync();
            registerId = register.Id;
        }

        await using var scoped = ScopedDbContext.ForTenant(
            postgres.AppConnectionString, tenantId, actorId, registerId);

        scoped.Services.GetRequiredService<IAuditLog>()
            .Record(AuditAction.ShiftClosed, nameof(Shift), Guid.NewGuid());

        await scoped.Db.SaveChangesAsync();

        var entry = Assert.Single(await ReadAsync(tenantId));

        // The composite foreign key is what makes this safe to record: a bare register_id
        // would accept another tenant's till, because referential-integrity checks are exempt
        // from row-level security.
        Assert.Equal(registerId, entry.RegisterId);
    }

    [Fact]
    public async Task An_empty_payload_is_stored_as_null_rather_than_an_empty_object()
    {
        var tenantId = Guid.NewGuid();

        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        scoped.Services.GetRequiredService<IAuditLog>()
            .Record(AuditAction.PinReset, nameof(ApplicationUser), Guid.NewGuid());

        await scoped.Db.SaveChangesAsync();

        var entry = Assert.Single(await ReadAsync(tenantId));

        // A PIN reset has no before and no after worth recording — the old and new hashes are
        // both secrets. "{}" in the column would read as "we captured the change and it was
        // empty", which is a different and untrue claim.
        Assert.Null(entry.Before);
        Assert.Null(entry.After);
    }

    private static Dictionary<string, string?>? Payload(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<Dictionary<string, string?>>(json);

    private async Task<List<AuditEntry>> ReadAsync(Guid tenantId)
    {
        await using var scoped = ScopedDbContext.ForTenant(postgres.AppConnectionString, tenantId);

        return await scoped.Db.AuditEntries.AsNoTracking().ToListAsync();
    }
}
