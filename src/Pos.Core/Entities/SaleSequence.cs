using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// The per-tenant sale-number counter. One row per tenant, incremented inside the sale's
/// own transaction.
/// </summary>
/// <remarks>
/// <b>A counter row and not a Postgres <c>SEQUENCE</c>, deliberately.</b> A sequence is
/// non-transactional: it advances even when the transaction that drew from it rolls back, so
/// a failed sale would burn a number permanently. Gaps in a financial series look like deleted
/// records to an auditor, and the shop cannot prove otherwise. Here a rolled-back sale rolls
/// back its own increment.
/// <para>
/// The cost, stated so it is not discovered under load: concurrent sales <i>within one tenant</i>
/// serialise on this row for the length of the sale transaction. For a shop with a handful of
/// tills that is the right trade — a gapless, human-quotable reference is worth more than
/// parallelism nobody will observe.
/// </para>
/// <para>
/// Machinery rather than a business record: it is written by one raw upsert that bypasses the
/// interceptor, so its audit columns say nothing and are not meant to. Row-level security
/// still applies.
/// </para>
/// </remarks>
public sealed class SaleSequence : TenantEntity
{
    /// <summary>The last number issued. The next sale takes this plus one.</summary>
    public long LastNumber { get; set; }
}
