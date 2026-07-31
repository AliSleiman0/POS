using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Pos.Core.Tenancy;

namespace Pos.Data.Interceptors;

/// <summary>
/// Publishes the ambient tenant to Postgres as <c>app.tenant_id</c>, which is what the
/// row-level security policies read. Layer 3 of the isolation in
/// docs/ARCHITECTURE.md#multi-tenancy.
/// </summary>
/// <remarks>
/// <b>Session-level <c>set_config</c>, deliberately not <c>SET LOCAL</c>.</b> <c>SET LOCAL</c>
/// is scoped to the enclosing transaction, and outside an explicit one that means the
/// implicit single-statement transaction — which ends immediately. EF Core reads do not
/// open transactions, so the setting would be gone before the query that needed it ran.
/// RLS would have looked configured and enforced nothing, with no error anywhere.
/// <para>
/// Session-level scoping is only safe because connection pooling resets it: Npgsql sends
/// <c>DISCARD ALL</c> when a connection returns to the pool. Two consequences that must not
/// be undone — the connection string must <b>not</b> set <c>No Reset On Close=true</c>, and
/// multiplexing must stay off, because a multiplexed connection interleaves statements from
/// different requests over one session. <c>PooledConnectionInheritsTenantTests</c> is the
/// test that fails if either changes.
/// </para>
/// <para>
/// The value is passed as a <b>parameter</b>, never interpolated. <c>set_config</c> exists
/// precisely so that a session variable can be set from a parameter; <c>SET</c> cannot take
/// one, which is how this becomes string concatenation and then an injection point.
/// </para>
/// </remarks>
public sealed class TenantConnectionInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    /// <summary>The GUC the policies read. Must match the RLS migration.</summary>
    public const string SettingName = "app.tenant_id";

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken).ConfigureAwait(false);

        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        // EF has synchronous paths (migrations, and any caller that did not await) which
        // would otherwise open a connection carrying no tenant at all.
        ApplyAsync(connection, CancellationToken.None).GetAwaiter().GetResult();

        base.ConnectionOpened(connection, eventData);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        if (connection is not NpgsqlConnection npgsql)
        {
            return;
        }

        // Written on every open, including when no tenant is resolved, and then written as
        // empty. Relying on DISCARD ALL alone to clear it would make correctness depend on
        // a pool setting held somewhere else; this way an unresolved scope actively states
        // that it has no tenant, and the policies see NULL and match no rows.
        var value = tenantContext.IsResolved
            ? tenantContext.TenantId.ToString()
            : string.Empty;

        await using var command = npgsql.CreateCommand();

        command.CommandText = "SELECT set_config(@name, @value, false)";
        command.Parameters.AddWithValue("name", SettingName);
        command.Parameters.AddWithValue("value", value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
