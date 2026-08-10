# Phase 7 — Employees, Roles & Audit

**Goal:** an owner can manage their own staff without contacting us, and there is a record of every action that moves money.

Per `DECISIONS.md`, store-level roles are **core MVP**, not a later nicety. A shop with more than one employee cannot use a POS where everyone has full access to prices, refunds and reports.

**Depends on:** Phases 1 and 4.

---

## 7.1 Employee management UI

`src/features/admin/employees/` + endpoints per [API.md](../API.md#employees--employees).

- List employees with role and status
- Create: display name, email, role, initial PIN
- Edit: name, role, active status
- Reset PIN
- **Deactivate, never delete.** Sales, audit entries and shifts reference users permanently.
- Register management: enroll a device (token shown **once**), revoke a lost device

All gated on `CanManageEmployees` (Owner). The gate is enforced server-side; the UI hiding it is convenience.

Guardrails worth building rather than discovering:
- An Owner cannot remove their own Owner role or deactivate themselves — a tenant locking itself out is a support call we cannot resolve without direct DB access (and there is no platform admin tool yet, per `DECISIONS.md`)
- At least one active Owner must remain
- PIN uniqueness within a tenant is **not** enforced, deliberately — an error saying "that PIN is taken" tells an attacker a valid PIN. Identity is established by picking your name *and* entering your PIN.

**Exit criteria**
- [x] Full employee lifecycle through the UI — `src/features/admin/employees/`, at `/admin/people`
- [x] Device enroll/revoke; token displayed once and unretrievable — `/admin/tills` is the fleet view; `POST /registers` and revoke had no UI before this
- [x] Self-demotion and last-Owner-removal blocked, with tests — three 409 slugs, and the last-owner count taken under `SELECT … FOR UPDATE`. Falsified by dropping the lock: the concurrent race then failed four runs out of four with the shop left ownerless
- [x] Deactivated user cannot log in or PIN in — and their refresh tokens are revoked. The ~15-minute access-token window is recorded in `docs/API.md` rather than pretended away
- [x] No PIN-collision error message — pinned structurally: two staff share a PIN, both `set-pin` calls return 204, and `POST /auth/pin` still resolves each to the right person

## 7.2 Audit log

`Pos.Core/Auditing/` + `AuditEntry` + `GET /audit`.

Recorded actions — the ones that cost money or conceal theft:

| Action | Captured |
|---|---|
| `PriceOverridden` | sale, line, original price, new price, actor |
| `DiscountApplied` | sale, line or cart, amount, actor |
| `SaleVoided` | sale, reason, actor |
| `RefundIssued` | original sale, refund sale, amount, actor |
| `StockAdjusted` | product, delta, reason, actor |
| `RoleChanged` | user, before, after, actor |
| `PinReset` | user, actor |
| `DeviceEnrolled` / `DeviceRevoked` | register, actor |
| `SettingsChanged` | key, before, after, actor |
| `ShiftClosed` | shift, variance, actor |

- Written in the **same transaction** as the audited action, so an audit entry cannot be missing for an action that succeeded (nor present for one that rolled back)
- `Before`/`After` as `jsonb`
- Append-only: no update or delete path exists, and the app's DB role has no `UPDATE`/`DELETE` grant on the table

**Deliberately not a change-log of everything.** An audit log that records every field edit is too noisy to read, so nobody reads it, so it does not function as an audit log. A targeted list stays useful.

Read UI at `src/features/admin/audit/` with filters by action, actor and date range.

Two actions were added beyond the table above, and one clarified:

- **`EmployeeCreated` / `EmployeeDeactivated`.** `ApplicationUser` carries no `CreatedBy`, so without these there is no record anywhere of who granted somebody access to the till — which is the same shape as everything else on the list.
- **`ReceiptIssued`**, which is what pays the Phase 6.2 debt: the reprint mark is now derived from the count of issues rather than declared by the client. See `DECISIONS.md`.
- **`AuthorizationRefused`** covers the sale-adjustment refusals only, not every `403` — a misconfigured client polling a forbidden route would bury the entries that matter.

**Exit criteria**
- [x] Every action above writes an entry, each with a test — and `AuditManifest` + `AuditCoverageTests` fail the build when an enum member has no row, so this keeps holding after the phase
- [x] Entries are transactional with their action — a rolled-back sale leaves no audit entry (`SaleAuditTests.A_sale_that_fails_after_pricing_leaves_no_entry`)
- [x] No update/delete path; DB grants confirm it — `AppendOnlyGrantTests` checks `has_table_privilege` *and* that a raw `UPDATE`/`DELETE` as `pos_app` raises `42501`. Falsified by removing the revoke: four tests red, and the `UPDATE` genuinely succeeded
- [x] Filterable read UI — `/admin/activity`, filtering by action, actor and trading-day range
- [x] `Before`/`After` populated for changes — as `jsonb` objects, so the client renders them without a second parse

## 7.3 Authorization tests

The phase's real deliverable: proof that permissions hold.

`tests/Pos.Api.Tests/Authorization/` — a matrix test over (role × endpoint):

- [x] A **Cashier** is rejected on every `CanManageCatalog`, `CanManageEmployees`, `CanViewMargins`, `CanRefund`, `CanVoidSale`, `CanApplyDiscount`, `CanOverridePrice` endpoint
- [x] A **Manager** is rejected on `CanManageEmployees` and `CanViewMargins` endpoints
- [x] An **Owner** is permitted everywhere
- [x] A request with **no** token → `401`; a valid token with an insufficient role → `403`
- [x] Discount/override fields in a `POST /sales` body from a Cashier → `403`, and the attempt is audited — `RefusedAdjustmentAuditTests`
- [x] Every endpoint has an explicit policy — **already existed** as `AuthorizationContractTests.Every_endpoint_states_its_own_authorization`, built in Phase 1.7. Extended, not duplicated

That last test is the one that keeps working as the codebase grows. The others verify today's endpoints; it verifies tomorrow's.

**The matrix is derived, not listed.** It crosses the routing table with `PolicyCatalog.RolesByPolicy`, so an endpoint mapped tomorrow is covered the day it ships and there is no second list to keep in step. Negatives are pinned exactly (401 for anonymous or the wrong *scheme*, 403 for the wrong role); a permitted caller is asserted only to get **neither** — `Guid.Empty` in every route parameter and an empty body mean a 404 or a 400, which proves authorization passed without writing anything. That is what lets 229 cases run against a shared world with nothing to reset.

**What it cannot catch**, verified rather than assumed: because the expectation is read off each endpoint's own metadata, moving `GET /audit` from `CanManageEmployees` to `CanSell` left all 229 green. The wrong-*policy* question is answered by the `Refused` lists in `IsolationManifest` and by hand-written tests — both of which did go red on that change. Recorded in `DECISIONS.md`.

**Exit criteria**
- [x] The matrix is exhaustive over current endpoints — and over future ones, by construction
- [x] The "every endpoint has a policy" test exists and passes
- [x] Negative paths are tested, not only happy ones

## 7.4 Settings

Added to the phase because §7.2 lists `SettingsChanged` as an audited action and there was no route to audit. `GET /settings` (`CanSell`) and `PUT /settings` (`CanManageEmployees`), plus `src/features/admin/settings/` at `/admin/settings`.

Until now the receipt header/footer/address/tax-number and the cash-rounding increment were reachable only through `tools/Pos.Seed`, which is a developer tool that is not shipped — so a customer could not change their own receipt footer at all.

- `taxMode` is **refused** once sales exist, not warned about, and the read side returns `taxModeLocked` so the screen renders it read-only with a reason
- Currency, time zone and the trading-day offset are shown and not editable — changing them rewrites what past reports meant
- One `SettingsChanged` entry per changed key; saving with nothing edited writes none

**Exit criteria**
- [x] An owner can change their receipt fields and rounding rule from a browser
- [x] `taxMode` refused after the first sale, with a stable slug
- [x] Each changed key audited separately, with before and after
- [x] `PUT` proven to touch only the calling tenant's row — the only write in the API with no query filter, no RLS policy and no interceptor behind it

---

## Verification

```powershell
dotnet test
dotnet run --project src/Pos.Api
pnpm --dir src/Pos.Web dev
```

Manual: as Owner create a Manager and a Cashier → log in as each → confirm the Cashier sees no catalog, employee, margin or refund controls, and that hitting those endpoints directly (via devtools or Swagger with the Cashier's token) is rejected → as Manager apply a discount and issue a refund → as Owner read the audit log and confirm both appear with the right actor.

Try to lock yourself out (remove your own Owner role) and confirm you cannot.
