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
- [ ] Full employee lifecycle through the UI
- [ ] Device enroll/revoke; token displayed once and unretrievable
- [ ] Self-demotion and last-Owner-removal blocked, with tests
- [ ] Deactivated user cannot log in or PIN in
- [ ] No PIN-collision error message

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

**Exit criteria**
- [ ] Every action above writes an entry, each with a test
- [ ] Entries are transactional with their action — a rolled-back sale leaves no audit entry
- [ ] No update/delete path; DB grants confirm it
- [ ] Filterable read UI
- [ ] `Before`/`After` populated for changes

## 7.3 Authorization tests

The phase's real deliverable: proof that permissions hold.

`tests/Pos.Api.Tests/Authorization/` — a matrix test over (role × endpoint):

- [ ] A **Cashier** is rejected on every `CanManageCatalog`, `CanManageEmployees`, `CanViewMargins`, `CanRefund`, `CanVoidSale`, `CanApplyDiscount`, `CanOverridePrice` endpoint
- [ ] A **Manager** is rejected on `CanManageEmployees` and `CanViewMargins` endpoints
- [ ] An **Owner** is permitted everywhere
- [ ] A request with **no** token → `401`; a valid token with an insufficient role → `403`
- [ ] Discount/override fields in a `POST /sales` body from a Cashier → `403`, and the attempt is audited (a rejected attempt is exactly what an owner wants to see)
- [ ] Every endpoint has an explicit policy — a test enumerates routes and **fails on any endpoint with no authorization metadata**, so a new endpoint cannot ship unprotected by omission

That last test is the one that keeps working as the codebase grows. The others verify today's endpoints; it verifies tomorrow's.

**Exit criteria**
- [ ] The matrix is exhaustive over current endpoints
- [ ] The "every endpoint has a policy" test exists and passes
- [ ] Negative paths are tested, not only happy ones

---

## Verification

```powershell
dotnet test
dotnet run --project src/Pos.Api
pnpm --dir src/Pos.Web dev
```

Manual: as Owner create a Manager and a Cashier → log in as each → confirm the Cashier sees no catalog, employee, margin or refund controls, and that hitting those endpoints directly (via devtools or Swagger with the Cashier's token) is rejected → as Manager apply a discount and issue a refund → as Owner read the audit log and confirm both appear with the right actor.

Try to lock yourself out (remove your own Owner role) and confirm you cannot.
