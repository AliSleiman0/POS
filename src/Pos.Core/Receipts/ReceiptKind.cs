namespace Pos.Core.Receipts;

/// <summary>
/// What a receipt is a receipt <i>for</i>.
/// </summary>
/// <remarks>
/// Not cosmetic, and not derivable by a renderer from the sign of the total. A refund receipt
/// has to say that it is one and name the sale it reverses; a customer handed a document
/// showing "−€12.30" and nothing else cannot tell it from a sale that went wrong, and neither
/// can the person who takes it back over the counter a week later. A voided sale's receipt has
/// to say <c>VOIDED</c> loudly enough that it cannot be presented as proof of purchase.
/// </remarks>
public enum ReceiptKind
{
    /// <summary>An ordinary completed sale.</summary>
    Sale,

    /// <summary>A return against an earlier sale. Amounts are negative.</summary>
    Refund,

    /// <summary>A sale that was reversed. Kept printable, because a void is a record too.</summary>
    VoidedSale,
}
