using Pos.Core.Entities;

namespace Pos.Api.Tests.Audit;

/// <summary>One audited action, and where the proof that it is written lives.</summary>
/// <param name="Action">The enum member.</param>
/// <param name="WrittenBy">The route or handler that appends it, for a reader.</param>
/// <param name="CoveredBy">
/// The test that proves it. Named as a string rather than referenced, because the point is that
/// somebody adding an enum member has to go and write one — a compiler reference would be
/// satisfied by any test at all.
/// </param>
public sealed record AuditCase(AuditAction Action, string WrittenBy, string CoveredBy);

/// <summary>
/// Every <see cref="AuditAction"/>, and the test that proves it is actually written.
/// </summary>
/// <remarks>
/// The same shape as <c>IsolationManifest</c>, and for the same reason: the per-action tests
/// verify today's behaviour, and this verifies tomorrow's. An audit log's failure mode is
/// silent — an action that quietly stops writing an entry produces a green suite and a log
/// that is missing exactly the thing somebody went looking for — so the guard has to be
/// "every member of the enum is accounted for", not "these fifteen tests pass".
/// <para>
/// Adding a member to <see cref="AuditAction"/> fails
/// <c>AuditCoverageTests.Every_audited_action_has_a_row</c> until a row is added here, and a
/// row with no test named fails the row beside it.
/// </para>
/// </remarks>
public static class AuditManifest
{
    public static IReadOnlyList<AuditCase> Cases { get; } =
    [
        new(
            AuditAction.PriceOverridden,
            "POST /sales — SaleEndpoints.RecordAdjustments, inside the writer's transaction",
            "SaleAuditTests.A_price_override_records_the_catalog_price_it_replaced"),
        new(
            AuditAction.DiscountApplied,
            "POST /sales — SaleEndpoints.RecordAdjustments, per line and once for the cart",
            "SaleAuditTests.A_line_discount_and_a_cart_discount_are_recorded_separately"),
        new(
            AuditAction.SaleVoided,
            "POST /sales/{id}/void — inside ISaleWriter.VoidAsync's transaction",
            "SaleAuditTests.Voiding_a_sale_records_the_reason"),
        new(
            AuditAction.RefundIssued,
            "POST /sales/{id}/refund — inside ISaleWriter.RefundAsync's transaction",
            "SaleAuditTests.A_refund_records_both_sales"),
        new(
            AuditAction.ReceiptIssued,
            "GET /sales/{id}/receipt — one per issue; the reprint mark is derived from the count",
            "ReceiptIssueTests.The_second_copy_of_a_receipt_is_marked_as_a_reprint"),
        new(
            AuditAction.StockAdjusted,
            "POST /stock/adjustments — inside the handler's own transaction",
            "StockAuditTests.An_adjustment_records_the_reason_and_the_resulting_count"),
        new(
            AuditAction.EmployeeCreated,
            "POST /employees",
            "EmployeeCrudTests.Creating_an_employee_is_audited_with_their_role"),
        new(
            AuditAction.EmployeeDeactivated,
            "POST /employees/{id}/deactivate, and PUT /employees/{id} with isActive false",
            "EmployeeAuditTests.Deactivating_somebody_is_audited_by_either_route"),
        new(
            AuditAction.RoleChanged,
            "PUT /employees/{id}",
            "EmployeeCrudTests.Changing_a_role_is_audited_with_both_sides"),
        new(
            AuditAction.PinReset,
            "POST /employees/{id}/set-pin",
            "EmployeeCrudTests.Resetting_a_PIN_is_audited_without_recording_the_PIN"),
        new(
            AuditAction.DeviceEnrolled,
            "POST /registers/{id}/enroll",
            "RegisterAuditTests.Enrolling_a_till_is_audited_without_recording_the_token"),
        new(
            AuditAction.DeviceRevoked,
            "POST /registers/{id}/revoke",
            "RegisterAuditTests.Revoking_a_till_is_audited"),
        new(
            AuditAction.SettingsChanged,
            "PUT /settings — one entry per changed key",
            "SettingsTests.Each_changed_setting_is_audited_separately"),
        new(
            AuditAction.ShiftClosed,
            "POST /shifts/{id}/close — inside ShiftWriter.CloseAsync's transaction",
            "ShiftAuditTests.Closing_a_drawer_records_the_variance"),
        new(
            AuditAction.AuthorizationRefused,
            "POST /sales — SaleEndpoints.CreateAsync, when an adjustment is not permitted",
            "RefusedAdjustmentAuditTests.A_cashiers_discount_is_refused_and_the_attempt_is_recorded"),
    ];
}
