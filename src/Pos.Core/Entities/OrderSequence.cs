using Pos.Core.Tenancy;

namespace Pos.Core.Entities;

/// <summary>
/// The per-tenant order-number counter. One row per tenant, incremented inside the opening
/// transaction.
/// </summary>
/// <remarks>
/// A counter row and not a Postgres <c>SEQUENCE</c>, for the reason
/// <see cref="SaleSequence"/> sets out at length: a sequence advances even when the transaction
/// that drew from it rolls back.
/// <para>
/// <b>Separate from <see cref="SaleSequence"/>, and that is the point.</b> One order can settle
/// as three bills and therefore three sales, and an abandoned order settles as none — so sharing
/// a counter would scatter the sale numbers with gaps that have no explanation on any row. Gaps
/// in a financial series read as deleted records to an auditor, which is precisely what the sale
/// counter exists to prevent. An order number is a thing staff shout across a room; a sale number
/// is a thing a tax inspector counts.
/// </para>
/// <para>
/// Machinery rather than a business record: written by one raw upsert that bypasses the
/// interceptor, so its audit columns say nothing and are not meant to. Row-level security still
/// applies.
/// </para>
/// </remarks>
public sealed class OrderSequence : TenantEntity
{
    /// <summary>The last number issued. The next order takes this plus one.</summary>
    public long LastNumber { get; set; }
}
