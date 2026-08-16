/**
 * The authorization policies, mirroring `src/Pos.Api/Auth/PolicyCatalog.cs` and
 * the table in `docs/ARCHITECTURE.md#authorization`.
 *
 * Endpoints require a *named policy*, never a role literal — a backend test
 * (`AuthorizationContractTests.No_endpoint_tests_a_role_literal`) fails the
 * build over one — and `GET /auth/me` returns the caller's list so the UI can
 * hide controls it cannot use.
 *
 * **The gate here is a courtesy.** The server re-checks every call, and fields
 * a role may not read are omitted from the response rather than hidden in the
 * UI (CLAUDE.md invariant 7). A disabled button is a suggestion.
 *
 * *Known gap:* `/auth/me` types `policies` as `string[]`, so this list is not
 * derived from the server and could drift from `PolicyCatalog`. Nothing in the
 * type system catches that. What catches it is the Playwright spec that logs in
 * as a Cashier and asserts the catalog controls are absent — if a policy is
 * renamed server-side, that test fails.
 */

export const POLICIES = [
  'CanSell',
  'CanApplyDiscount',
  'CanOverridePrice',
  'CanVoidSale',
  'CanRefund',
  'CanManageCatalog',
  'CanViewMargins',
  'CanManageEmployees',
  'CanCloseShift',

  // Phase 10. Taking an order and working the pass are everyone's; changing the
  // room or cancelling cooked food is a supervisor's. See PolicyCatalog.
  'CanTakeOrders',
  'CanWorkKitchen',
  'CanVoidFiredLine',
  'CanManageFloor',
] as const

export type Policy = (typeof POLICIES)[number]

export function hasPolicy(granted: readonly string[], policy: Policy): boolean {
  return granted.includes(policy)
}
