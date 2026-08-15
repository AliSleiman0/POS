namespace Pos.Core.Entities;

/// <summary>
/// What happened. Deliberately a short, closed list of the actions that move money or
/// conceal theft — not a change-log of every field edit.
/// </summary>
/// <remarks>
/// Stored as text through <c>HasEnumAsText</c>, so these names are the values in
/// <c>ck_audit_entry_action_allowed</c> and in every row already written. Renaming a member
/// rewrites the constraint in the next migration and orphans the history it was written to
/// preserve — treat these names as a wire contract, the same way
/// <see cref="StockMovementType"/> does.
/// <para>
/// An audit log that records everything is too noisy to read, so nobody reads it, so it does
/// not function as an audit log. Adding a member is a deliberate decision, and the audit
/// manifest in the API tests fails the build until a test proves the new action is written.
/// </para>
/// </remarks>
public enum AuditAction
{
    /// <summary>A line sold at a price other than the catalog's, with a manager's approval.</summary>
    PriceOverridden,

    /// <summary>A discount applied to a line or to the cart.</summary>
    DiscountApplied,

    /// <summary>A completed sale voided. The cash goes straight back.</summary>
    SaleVoided,

    /// <summary>Money returned against an earlier sale.</summary>
    RefundIssued,

    /// <summary>
    /// A receipt handed over — the first time or any time after. What makes a reprint
    /// server-known rather than client-declared, and therefore countable.
    /// </summary>
    ReceiptIssued,

    /// <summary>Stock moved by hand rather than by a sale.</summary>
    StockAdjusted,

    /// <summary>
    /// Somebody was given a way into the shop. <c>ApplicationUser</c> carries no
    /// <c>CreatedBy</c>, so without this there is no record of who granted till access.
    /// </summary>
    EmployeeCreated,

    /// <summary>Somebody's access was withdrawn.</summary>
    EmployeeDeactivated,

    /// <summary>An employee's role changed, and with it everything they may do.</summary>
    RoleChanged,

    /// <summary>An employee's PIN was set or replaced by somebody other than them.</summary>
    PinReset,

    /// <summary>A till was issued a device token.</summary>
    DeviceEnrolled,

    /// <summary>A till's device token was invalidated.</summary>
    DeviceRevoked,

    /// <summary>A shop-wide setting changed. One entry per key.</summary>
    SettingsChanged,

    /// <summary>A drawer was counted and closed, with whatever variance that produced.</summary>
    ShiftClosed,

    /// <summary>
    /// Somebody tried something they were not permitted to do. A refused attempt is exactly
    /// what an owner wants to see, and it leaves no other trace.
    /// </summary>
    AuthorizationRefused,

    /// <summary>
    /// A table was seated or a tab started.
    /// </summary>
    /// <remarks>
    /// Recorded because it is the start of the only trail that explains an abandoned order or a
    /// table that was open for four hours, and because <c>Order.OpenedBy</c> alone says who but
    /// not that anything happened at a particular moment.
    /// </remarks>
    OrderOpened,

    /// <summary>
    /// An item already sent to the kitchen was taken off an order.
    /// </summary>
    /// <remarks>
    /// The restaurant's <see cref="SaleVoided"/>: the moment food leaves without money arriving.
    /// A <i>pending</i> line coming off is not recorded — nobody cooked it, and filing an entry
    /// every time a customer changes their mind is how a log stops being read.
    /// </remarks>
    OrderLineVoided,

    /// <summary>An order moved to another table or became a tab.</summary>
    OrderTransferred,

    /// <summary>One order's items were absorbed into another.</summary>
    OrderMerged,

    /// <summary>
    /// An order was written off unpaid — a walk-out, or a mistake.
    /// </summary>
    /// <remarks>
    /// Deliberately distinct from a close. Whatever was fired was cooked and is gone, and a shop
    /// needs that visible as a loss rather than folded into a day's takings.
    /// </remarks>
    OrderAbandoned,
}
