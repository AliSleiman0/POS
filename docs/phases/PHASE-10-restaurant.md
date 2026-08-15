# Phase 10 — Restaurant mode

**Goal:** a shop set to restaurant mode can seat a table, take an order over an hour, send it to
the kitchen in rounds, split the bill, take cash with a tip, and reconcile in the same Z-report a
retail shop uses — with the retail path untouched.

**Why now:** the MVP (Phases 0–8) proved the money path, and Phase 9 proved it offline. Restaurant
is `DECISIONS.md`'s V2, and its constraint is the one that shapes this phase: *"Retail and
restaurant have genuinely different data models (simple 'sale' vs. 'order' with courses/seats/
modifiers) — do not force one schema for both prematurely."* CLAUDE.md restates it as **"restaurant
mode is a separate model, not a bolt-on to `Sale`."**

**Depends on:** Phases 0–8. Critically on **3.5 (idempotency)** and **3.6 (`ISaleWriter`)** —
paying a bill is an ordinary sale commit, and without those this phase would be a second write
path for money.

---

## The five rules

Everything below follows from these. Change one and the phase is a different phase.

1. **An order is not financial.** It is mutable, editable, voidable; nothing in it is append-only.
   When money changes hands a `Sale` is committed and *that* is append-only — invariant 4 is
   untouched. `OrderBill.SaleId` links the two, and it points **from** the restaurant model **to**
   `Sale`, never the reverse. `Sale` never learns what an order is.
2. **One pricing engine.** A bill is priced by `Pos.Core.Pricing.PricingEngine` and committed by
   `ISaleWriter`. No second money path — the same argument Phase 9 used to refuse a bulk sync
   endpoint: a second implementation is a second place the totals can be wrong, and the two
   disagree about a customer's bill eventually.
3. **Prices snapshot when the item is ordered**, not when the bill is paid. A guest who ordered at
   18:00 pays the 18:00 price if the menu changes at 19:00. `OrderLine` carries `Description`,
   `UnitPrice` and `TaxRate` snapshots exactly as `SaleLine` does (invariant 5).
4. **Stock moves at payment, not at fire.** `IStockLedger` stays the single writer, inside the
   sale's transaction. A fired-then-voided item is a `Waste` movement if the shop records one — it
   is not an automatic reversal, because inventing compensating movements for food that may or may
   not have been cooked is worse data than none.
5. **Splitting evenly is a tender split, not a bill split.** `Tender` is already a collection and
   5.4 already ships split tender with a running balance, so an even split is N cash tenders
   against one sale — no new mechanism and no fractional-quantity rounding. Splitting *by item or
   seat* is what needs bills, and there the allocated quantities are validated to sum exactly to
   the order line's quantity.

## What restaurant mode costs a shop

**No offline order-taking.** An order lives on the server so a second tablet can see the table;
the retail cart lives in `sessionStorage` so it can be rung with the line down. Phase 9's outbox
queues *sales* and knows nothing about orders. A restaurant till with no network cannot open or
add to an order, and the app says so in `OfflineLimitsPanel` in the same unflattering register as
the rest of 9.5 — it is not something a service should discover.

---

## 10.0 Service mode

`Tenant.ServiceMode` (`Retail` | `Restaurant`), defaulting to `Retail`.

- Surfaced on `GET`/`PUT /settings` and mirrored in `GET /catalog/sync`'s settings block, so a
  till that has lost the network still knows which product it is.
- **Changeable, unlike `TaxMode`**, and the difference is what each one reinterprets: a tax mode
  decides what every stored price *means*; a service mode decides which screens a shop sees. A
  `Sale` written in one mode reads identically in the other because it is the same row.
- **Required on the request, not defaulted.** An omitted enum binds to its zero member — `Retail`
  — so a client that had never heard of the field would post a complete-looking body that turned a
  restaurant back into a counter. The same trap `CloseShiftRequest.CountedCash` already guards.

**Exit criteria**
- [x] `ServiceMode` on the tenant, defaulted in the column so every pre-existing shop is `Retail`
- [x] Readable and writable at `/admin/settings`, audited as `SettingsChanged` per changed key
- [x] Mirrored to the offline till through `GET /catalog/sync`
- [x] A body omitting `serviceMode` is refused rather than silently defaulted — its own test
- [x] Switching **to** `Retail` with orders still open is refused — `409 orders-still-open` (landed in 10.2, where `Order` exists)

## 10.1 Order model

`ServiceArea`, `DiningTable`, `Order`, `OrderLine`, `OrderSequence` — all `TenantEntity`, one
`IEntityTypeConfiguration<T>` each, composite foreign keys carrying the tenant, every index
leading with `TenantId`.

`OrderWriter` in `Pos.Data/Orders/` is a **public class, not a Core port** — the call `ShiftWriter`
already documents: a transaction with a lock is not a repository, and Core has no rule to own here.

**Exit criteria**
- [x] Entities, configurations and a reviewed migration — five tables, all `CREATE`, nothing destructive
- [x] Order numbers from a per-tenant counter incremented in-transaction, never `MAX()+1`. **Its own series, separate from the sale's**: one order can settle as three sales and an abandoned one settles as none, so sharing would scatter unexplained gaps through the financial series
- [x] Tenant isolation and RLS proved for every new table — `ApplyTenantRowLevelSecurity()` called in the migration, and `Every_tenant_owned_table_is_covered` is what would have failed
- [x] At most one open order per table, enforced by a **filtered unique index** rather than a pre-check, and proved with two writers racing — `ux_customer_order_tenant_table_open`
- [x] Line numbers allocated under a `FOR UPDATE` on the order row, from `MAX` rather than a count, so a void does not hand its number to the next item

**The table naming is deliberate.** `ORDER` and `TABLE` are both reserved words in SQL, so the tables are `customer_order` and `dining_table`. This codebase writes raw SQL in the money paths — EF cannot aggregate a value-converted `Money` — and a name that works only while everybody remembers to quote it is a name that will eventually not be quoted.

## 10.2 Orders API

`POST /orders` · `GET /orders` · `GET /orders/{id}` · `POST /orders/{id}/lines` ·
`PATCH /orders/{id}/lines/{lineId}` · `POST /orders/{id}/lines/{lineId}/void` ·
`POST /orders/{id}/transfer` · `POST /orders/{id}/merge` · `POST /orders/{id}/abandon`

New policies: `CanTakeOrders` (everyone), `CanVoidFiredLine`, `CanManageFloor` (supervisors),
`CanWorkKitchen` (everyone). Mirrored into `docs/ARCHITECTURE.md#authorization`, which a test
checks.

Plus the room itself, which the floor screen cannot exist without: `GET /floor`,
`POST`/`PUT /floor/areas`, `POST`/`PUT /floor/tables`.

**Exit criteria**
- [x] Every route has an `IsolationManifest` row — fourteen of them; `EndpointCoverageTests` fails the build without one
- [x] A negative-authorization test per endpoint, derived from `PolicyCatalog` × the routing table
- [x] Order routes on a retail tenant answer `409 restaurant-mode-required` — a state conflict, not an authorization failure, and the route stays mapped so the coverage tests still see it
- [x] Switching a shop to `Retail` with open orders is refused — `409 orders-still-open`
- [x] Voiding a **fired** line takes `CanVoidFiredLine` and a reason and is audited; voiding a **pending** one takes neither and is not
- [x] A modifier is one level deep, travels with its parent when voided, and cannot itself carry modifiers
- [x] Prices snapshot at order time — asserted by changing the menu underneath an open order

**A real defect fell out of this milestone, in shipped Phase 9 code.** `GET /catalog/sync` read its settings block with `db.Tenants.FirstOrDefaultAsync()` and **no `Where`**. `Tenant` is not tenant-owned, so it carries no query filter, no RLS policy and no interceptor check — the scoping is one clause the handler has to contain, and it did not. Every till mirrored an arbitrary shop's currency, tax mode, rounding increment and receipt address; offline, `taxMode` is an input to the pricing engine, so the till would have priced carts by another business's rules.

It survived a passing test because the isolation world seeds both tenants **identically** — deliberately, so a leak doubles a list — which makes the two possible answers the same string in every field that test asserted on. The first field that differed was `serviceMode`, added in 10.0. The lesson is recorded in `CatalogSyncTests`: *a fixture built to make leaks obvious by duplication hides them in any field it duplicates*, so the new test sets up two shops that disagree about everything it checks.

## 10.3 Modifiers

`Product.IsModifier`, `ModifierGroup`, `ModifierOption`, `ProductModifierGroup`.

A modifier **is** a product, so it gets a price, a tax class and optional stock tracking for free
and prices through the same engine. The flag keeps modifiers out of the register grid, the product
list and barcode search, and rides `GET /catalog/sync` so the offline mirror excludes them too. A
selected modifier becomes a child `OrderLine` with `ParentOrderLineId` set, snapshotting its own
description, price and rate.

**Exit criteria**
- [ ] `ModifierRules` is pure, in `Pos.Core`, and enforced at the API — the UI's gating is a courtesy
- [ ] A required group with nothing chosen refuses the line
- [ ] Modifier products never appear in the retail register or in barcode search

## 10.4 Courses, seats, stations, kitchen display

`Station`; routing resolved by `StationRouting` — `Product.StationId` when set, otherwise
`Category.StationId`, otherwise unrouted (which the fire endpoint refuses, naming the product).

`POST /orders/{id}/fire` creates one `KitchenTicket` per station in a single transaction, with
`KitchenTicketLine` rows snapshotting the description and composed modifier text. **A ticket is an
append-only record of what the kitchen was told** — not a view over the order line's current state,
because the line can be voided afterwards and the kitchen still cooked it.

The display polls. There is no SSE and no websocket in this project, and adding a transport is its
own phase; the interval is a stated, tunable number rather than an oversight.

**Not built.** See *What is not done*. `OrderLineStatus.Fired` and `OrderLine.FiredAt` exist and are
respected everywhere — a fired line cannot be amended, and voiding one takes `CanVoidFiredLine`,
a reason and an audit entry — but nothing sets them except a test, because there is no fire
endpoint and no station yet.

**Exit criteria**
- [ ] Firing is idempotent — a double-tap does not double-cook
- [ ] Each station's ticket holds only its own items
- [x] Voiding a fired line needs `CanVoidFiredLine` and a reason, and is audited; voiding a pending one needs neither and is not
- [ ] Bump and recall
- [ ] The display is asserted in Playwright, not only in jsdom

## 10.5 Bills, splitting, payment

`OrderBill`, `OrderBillLine`. Allocation by item or by seat, **validated so each order line's
allocated quantities sum exactly to its quantity** — a residue is refused, not absorbed. Paying
prices the bill through `PricingEngine` and commits through `ISaleWriter` with the bill's own
`ClientTransactionId` and `Idempotency-Key`.

**Exit criteria**
- [x] Paying a bill writes an ordinary `Sale` — number from the same counter, lines, tender, stock movement. Nothing about it is restaurant-shaped, and `OrderBill.SaleId` is the only link
- [x] A bill cannot be paid twice; a replay under the bill's own key returns the original sale
- [x] An order cannot close with unbilled lines, and closes by itself when the last bill is paid
- [x] Over-allocating a line is refused rather than clamped
- [x] A shared line carries its discount proportionally, so the parts still sum to the whole
- [x] A bill prices from the order line's snapshots, not the catalog — proved by changing the menu underneath
- [x] An even split is N tenders on one sale, and no new endpoint
- [x] A paid bill cannot be torn up — that is a refund against the sale, which Phase 3.7 owns
- [ ] Two devices billing the same line concurrently is deterministic, and tested concurrently

**One thing was corrected during the build.** The pay handler first took the register from the
session's `register_id` claim, which would have meant only a PIN-logged-in till could settle a
table. `POST /sales` and `POST /sales/{id}/refund` both take the register and shift **in the
body**, and for a stated reason — the money goes into the drawer that is open *now*, not the one
the order was opened at. The endpoint follows that convention instead of inventing a second one.

## 10.6 Tips

`Sale.TipAmount`; `ChangeGiven` becomes `tendered − total − tip`.

**DATA-MODEL invariant 2 is amended** to `sum(Tender.Amount) >= Sale.Total + TipAmount`, recorded
rather than quietly changed. `ShiftArithmetic.ExpectedCash` needs no change — a tip reduces change
given, so it is already inside net cash tendered — but the Z-report gains an explicit **Tips** line,
because a drawer over by exactly the tips must read as that rather than as an unexplained surplus.

Not gated on service mode: a tip jar at a counter is the same fact.

**Exit criteria**
- [x] A tipped sale's drawer reconciles, and the Z-report says where the extra cash came from — asserted both ways round, so the arithmetic a manager does in their head is the one being checked
- [x] A tender covering the bill but not the tip is refused, and says which it was short of
- [x] A negative tip is a field error
- [x] `ShiftArithmetic` is untouched, which is the point of taking the tip out of the change rather than adding it to the total
- [ ] A tip on a refund is refused — the refund path does not accept one, so there is nothing to refuse yet; it needs a test that says so
- [ ] The invariant holds as a property test, not only in the cases somebody tried

## 10.7 Restaurant UI

`features/restaurant/`: floor view, order screen (courses, seats, modifier sheet, per-course fire),
bill screen reusing `TenderPanel` and `SaleCompletePanel`.

**Order state is server-owned** (TanStack Query) — a deliberate departure from
`CartProvider`/`sessionStorage`, because a table's order has to be visible from a second device.
Invariant 11 is not weakened: nothing new goes to storage, and the manager's grant still lives only
in `OverrideProvider`.

**Exit criteria**
- [ ] `/register` picks the screen from `serviceMode`; a test asserts the retail register is unchanged
- [ ] No blocking browser dialogs anywhere in it (invariant 10)
- [ ] Every write idempotent, with the key minted once per operation (invariant 6)
- [ ] `OfflineLimitsPanel` states that orders need the server

## 10.8 Reporting

Takings by table, by server, tips by server, average cover, table turn time — joined through
`OrderBill → Sale`, never to current catalog data.

**Exit criteria**
- [ ] Restaurant sections appear only in restaurant mode
- [ ] A shift's report and the day's report that contains it still add up to the same money

## 10.9 Tests

Per invariant 9 these land **with each milestone**. This list is the floor, not the plan.

---

## Verification

1. Set the shop to restaurant mode; confirm `/register` shows the floor and a retail tenant is
   unchanged.
2. Seat table 4 with 3 covers. Order a starter and two mains, one with a required modifier group —
   confirm the line is refused until the group is satisfied.
3. Fire course 1. Confirm tickets reach the grill and the bar, each holding only its own items,
   with the modifier text.
4. Bump the bar ticket. Void a fired grill line with a reason; confirm the recall appears and the
   audit log records it.
5. Split by seat into two bills. Confirm the allocation is refused while a line is partly
   unallocated.
6. Pay bill 1 in cash with a €5 tip; pay bill 2 evenly between two people as two tenders.
7. Confirm the table returns to free, the order is `Closed`, and two `Sale` rows exist with correct
   totals and one tip.
8. Close the drawer. Confirm expected cash includes the tip and the Z-report shows it on its own
   line — the reconciliation being *legible* is what is being checked.
9. Take the network down mid-order. Confirm the app says plainly that orders need the server rather
   than losing the round silently.

## Non-goals

Stated up front so they are not read as oversights:

- **No reservations or waitlist.** A different product surface with its own model.
- **No floor-plan designer.** Tables are a list with an area and a sort order. A drag-and-drop
  canvas is a week of work that changes nothing about whether the till is correct.
- **No coursing timers or pacing.** Firing is manual and explicit.
- **No printer hardware.** The kitchen display is the surface; Phase 6.2's limitation on real
  thermal printers is unchanged.
- **No card payments and no tip-on-card.** Cash only, per `DECISIONS.md` — Phase 11 was dropped.
- **No offline orders.** See *What restaurant mode costs a shop* above.

## What is not done

**Written down rather than quietly left off the list**, in the shape of Phase 9's section. The
phase is **not finished**. What exists is the whole server side of an order's life — seat, order,
amend, void, transfer, merge, abandon, split, pay — and it is tested end to end against a real
Postgres. What does not exist is anything a person can look at.

1. **The kitchen (10.4).** No `Station`, no `StationRouting`, no `KitchenTicket`, no fire
   endpoint, no display. `OrderLineStatus.Fired` and `OrderLine.FiredAt` exist and everything
   downstream respects them — a fired line cannot be amended, and voiding one takes a supervisor,
   a reason and an audit entry — but **nothing sets them except a test**. This is the largest
   single piece left and it is self-contained: the columns and the rules it needs are already
   there.

2. **The entire restaurant UI (10.7).** `/register` still renders the retail screen for every
   tenant; `serviceMode` is readable at `/admin/settings` and changes nothing else. There is no
   floor view, no order screen, no modifier sheet, no bill screen, and no `/catalog/modifiers`
   admin page. **A shop cannot use any of this today** — every endpoint below works and none of
   them has a caller.

3. **Restaurant reporting (10.8).** Takings by table, by server, tips by server, average cover,
   table turn. The Z-report *does* carry the tips line from 10.6, which is the part that affects
   whether a drawer reconciles; the rest is analysis nobody is blocked on.

4. **The e2e spec and the seed fixture (10.9).** No `restaurant.spec.ts`, and `Pos.Seed` has no
   `--restaurant` flag — so there is no way to click through this by hand either, which is why
   item 2 has not been checked in a browser. Per `docs/ROADMAP.md`, **UI testing must never
   accumulate**, so 10.7 and 10.9 belong to the same piece of work rather than one following the
   other.

5. **Two devices billing the same order line at once.** The allocation check reads the running
   total and then inserts, which is check-then-act — two waiters splitting one table
   simultaneously could over-allocate a line. The unique index on
   `(tenant, bill, order_line)` stops the *same* line joining one bill twice, and nothing here
   can produce a wrong charge on a bill that is actually paid, but the guard is weaker than the
   filtered-index one used for seating and it is not yet proved concurrently.

Also **deliberate, not a gap**: everything under *Non-goals* above.
