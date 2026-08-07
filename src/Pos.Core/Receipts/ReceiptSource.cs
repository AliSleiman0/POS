using Pos.Core.Entities;

namespace Pos.Core.Receipts;

/// <summary>
/// Everything <see cref="ReceiptBuilder"/> needs, gathered by the caller.
/// </summary>
/// <remarks>
/// The sale's own rows, verbatim — not a parallel set of DTOs. Two shapes describing one sale
/// would have to be kept in step, and the place a drift between them surfaces is a customer
/// disputing a receipt.
/// <para>
/// <b>There is deliberately no <c>Product</c> here.</b> Every amount and every description on a
/// receipt comes from <see cref="SaleLine"/>, which snapshotted them at the moment of sale
/// (CLAUDE.md invariant 5). Leaving the catalog out of the input is what makes "the receipt
/// cannot join to a current price" structural rather than a rule someone has to remember.
/// </para>
/// </remarks>
/// <param name="Sale">The header row. Its amounts are the only ones already at payable scale.</param>
/// <param name="Lines">This sale's lines. Ordered by the builder, so the caller need not be.</param>
/// <param name="Tenders">What was handed over, and what came back as change.</param>
/// <param name="Shop">The tenant, for the header block and the currency.</param>
/// <param name="CashierName">
/// Who served the customer. A <b>current</b> value read from the user record, not a snapshot —
/// a renamed cashier is the same person, and a receipt naming who they used to be would be
/// wrong in a way nobody wants. Do not "fix" this to a snapshot; the amounts are the things
/// invariant 5 is about.
/// </param>
/// <param name="RegisterName">Which till. Current, for the same reason.</param>
/// <param name="OriginalSaleNumber">
/// The sale this one reverses, for a refund. Null otherwise. A number rather than an id,
/// because it is printed for a human to quote.
/// </param>
public sealed record ReceiptSource(
    Sale Sale,
    IReadOnlyList<SaleLine> Lines,
    IReadOnlyList<Tender> Tenders,
    Tenant Shop,
    string CashierName,
    string RegisterName,
    long? OriginalSaleNumber);
