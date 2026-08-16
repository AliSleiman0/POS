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
- [x] `ModifierRules` is pure, in `Pos.Core`, and enforced at the API — the UI's gating is a courtesy
- [x] A required group with nothing chosen refuses the line
- [x] Modifier products never appear in the retail register or in barcode search — the list, the offline mirror **and** `GET /products/by-barcode/{code}`, which was the gap. Closed in 10.7 with `ModifierTests.A_barcoded_modifier_does_not_resolve_at_the_scanner`

**The boxes above were unticked long after the work landed**, and the third one turned out to be a
real gap rather than an oversight in the list. The tests for the first two existed from the day
10.3 shipped; nobody went back to the checklist, and the one item that was not done hid among the
two that were.

## 10.4 Courses, seats, stations, kitchen display

`Station`; routing resolved by `StationRouting` — `Product.StationId` when set, otherwise
`Category.StationId`, otherwise unrouted (which the fire endpoint refuses, naming the product).

`POST /orders/{id}/fire` creates one `KitchenTicket` per station in a single transaction, with
`KitchenTicketLine` rows snapshotting the description and composed modifier text. **A ticket is an
append-only record of what the kitchen was told** — not a view over the order line's current state,
because the line can be voided afterwards and the kitchen still cooked it.

The display polls. There is no SSE and no websocket in this project, and adding a transport is its
own phase; the interval is a stated, tunable number rather than an oversight.

Routing is set on a **category** and inherited, because a restaurant will configure eight
categories and will not configure four hundred products — `StationRouting` walks up from the
product and takes the first station it finds. `Product.StationId` is the override for the one
bottled cocktail finished at the pass. **Unrouted is a real answer, not a default station:**
falling back to "the first station" sends a steak to the bar silently and the first anybody knows
is a customer asking after forty minutes.

Bump and recall are `POST /kitchen/tickets/{id}/bump` and `/recall`, and they carry **no
`Idempotency-Key`** — they move no money and no stock, and they are idempotent by state rather
than by a stored response. Bumping a bumped ticket is already a no-op, and requiring a key on
something a chef does forty times an hour would be friction that buys nothing.

**Nothing in the kitchen is audited.** `AuditAction` is deliberately a short list of the actions
that move money or conceal theft, and firing is neither. What is recorded is the void afterwards,
which is where a plate leaves without being paid for — so no new enum member, and no migration for
one.

**Exit criteria**
- [x] Firing is idempotent — a double-tap does not double-cook. Guaranteed by the line's **status**, changed in the same transaction that writes the ticket, so it holds for a second handheld carrying its own key; proved concurrently in `KitchenTicketWriterTests`, and the endpoint's key is what makes a genuine retry replay the original tickets rather than answer "nothing to fire"
- [x] Each station's ticket holds only its own items — one ticket per station **per course**, because a ticket spanning rounds is a queue the pass cannot pace
- [x] Voiding a fired line needs `CanVoidFiredLine` and a reason, and is audited; voiding a pending one needs neither and is not
- [x] Bump and recall
- [x] Something with no station refuses the **whole fire**, naming the product — firing half a round would put the rest of the table in the kitchen with no record of what was dropped
- [x] A ticket is append-only: a line voided afterwards is struck through on the screen, and a product renamed mid-service does not rewrite what the grill was told
- [x] The display is asserted in Playwright, not only in jsdom — `restaurant.spec.ts` fires a round, finds it on the grill's screen with its modifier text, bumps it, and finds a line voided afterwards struck through rather than gone

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
- [x] Two devices billing the same line concurrently is deterministic, and tested concurrently — the allocation read-then-insert now runs under the same `FOR UPDATE` on the order row that line numbering and firing take, and `OrderBillTests.Two_waiters_splitting_one_table_at_once_cannot_over_allocate_a_line` races two real clients through it

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
- [x] A tip on a refund is refused — `RefundSaleRequest` has no field to bind one to, and `TipTests.A_tip_cannot_be_attached_to_a_refund` sends one anyway and asserts it reaches neither the response nor the row
- [x] The invariant holds as a property test, not only in the cases somebody tried — `TipTenderRulesTests.Tendered_less_the_total_and_the_tip_is_always_the_change` walks a grid of totals, tips and over-tenders including a cent short at every boundary

## 10.7 Restaurant UI

`features/restaurant/`: floor view, order screen (courses, seats, modifier sheet, per-course fire),
bill screen reusing `TenderPanel` and `SaleCompletePanel`.

**Order state is server-owned** (TanStack Query) — a deliberate departure from
`CartProvider`/`sessionStorage`, because a table's order has to be visible from a second device.
Invariant 11 is not weakened: nothing new goes to storage, and the manager's grant still lives only
in `OverrideProvider`.

**Exit criteria**
- [x] `/register` picks the screen from `serviceMode`; a test asserts the retail register is unchanged — one route rather than two, because staff are trained on "the register". `RegisterPage` is wrapped and never edited
- [x] No blocking browser dialogs anywhere in it (invariant 10) — `noBlockingDialogs.test.ts` scans `features/restaurant` too, added in the commit that created the folder rather than after it
- [x] Every write idempotent, with the key minted once per operation (invariant 6) — `useOperationKey`, `useState` with a lazy initialiser rather than `useMemo`, which React may discard and recompute
- [x] `OfflineLimitsPanel` states that orders need the server, and the floor says so too — on the screen somebody is actually standing at when it happens

**Two things the build turned up.** `POST .../pay` returned no change due, so the bill screen would
have needed a second call at the moment a waiter is counting notes into a hand; `ChangeGiven` now
comes back on the pay response, out of the commit that computed it. And **the restaurant never
offered to open a drawer** — a retail till shows `OpenShiftPanel` on the register and a floor is
not a till, so a shop could seat, order and fire all evening and discover at the first bill that no
drawer existed, with no control anywhere to open one. The bill screen now surfaces it. The e2e
walk-through is what found that, which is the argument for writing it in the same phase.

## 10.8 Reporting

Takings by table, by server, tips by server, average cover, table turn time — joined through
`OrderBill → Sale`, never to current catalog data.

**Exit criteria**
- [x] Restaurant sections appear only in restaurant mode — the queries run only for a restaurant, so a counter's report costs exactly what it did before, and the sections come back **empty rather than absent** so no reader branches on a nullable
- [x] A shift's report and the day's report that contains it still add up to the same money — one `ReportScope` over one set of aggregations, only the window differs, asserted in `RestaurantReportTests`

**Spend per cover is null where nobody keyed a cover count**, not the takings. A takeaway
legitimately has none, and dividing by a missing denominator would put a plausible, wrong number on
a report somebody makes decisions with. Turn time is null while a table is still occupied, for the
same reason.

**A table's name is read live**, so a table renamed from "4" to "Window" reports its history under
the new name. Stated rather than hidden: unlike a price, a name is what a person uses to find the
row, and a report naming a table nobody recognises is worse than one that follows the rename. A
kitchen ticket, read at a pass mid-service, does snapshot it — different consumer, different
answer.

## 10.9 Tests and the seed fixture

Per invariant 9 these land **with each milestone**. This list is the floor, not the plan.

**`Pos.Seed --restaurant` exists.** One command puts the shop into restaurant mode and gives it
three stations, two areas with eight tables, a nine-item menu routed through its categories, and
two modifier groups — one of them required, so the refusal can be seen by hand. Like
`--cash-rounding`, it **changes an existing tenant**: a mode that only applied to a shop which did
not exist yet would be no use to somebody who has been testing against `corner-shop` all week.

The menu is small and every row earns its place. "Food" routes to the pass and "Starters" and
"Desserts" set nothing, so those two only reach a station by the walk up the chain — the part of
`StationRouting` a flat menu would never exercise. "Mains" overrides its parent and goes to the
grill.

**Exit criteria**
- [x] A room, a kitchen and a menu from one command, re-runnable without damage
- [x] `restaurant.spec.ts` walking the *Verification* list below in a browser

**A second e2e tenant, `e2e-restaurant`, rather than flipping the existing one.** Every retail spec
assumes `/register` is a till with a cart on it, so switching the shared shop would have broken all
of them to test one — and the second tenant is also what lets the spec assert the *retail* shop is
untouched while sharing a database with a restaurant that has just traded through a service.

**One test, not one per screen.** A test per screen makes each depend on the last one's leftovers,
and the database outlives the run: the second execution starts with table 4 already seated. The
spec walks the service once and settles what it opened, which is also the state the next run needs
to find.

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

**The phase is finished.** Every exit criterion above is ticked, the nine-step *Verification* walk
is a Playwright spec, and a shop set to restaurant mode can be seated, ordered for, fired to a
kitchen, split, settled with a tip and reconciled — through screens, by a person.

What is left is written down here rather than left to be discovered:

1. **Nobody who has worked a restaurant has used it.** That was the bar before Phase 9, it was the
   bar in every handoff since, and shipping a green test suite does not clear it. The floor, the
   modifier sheet and the pass are all guesses about how a room works until somebody who works one
   disagrees with them.

2. **The tender pad's provisional change ignores the tip.** `TenderPanel` computes a running
   balance from the total alone, so during a tipped bill it reads high by the tip until the server
   answers. Every figure on it is labelled provisional and the settled panel shows the server's
   number — but a cashier counting notes reads the pad, so this is worth fixing before a real shop
   uses it. It was not folded into 10.7 because the panel is shared with the retail till and the
   change is to a money path with paying users on it.

3. **No admin screen for stations, modifier groups or the floor.** All three are reachable through
   the API and seeded by `Pos.Seed --restaurant`; a manager cannot add a table or re-route a
   category without one. Not blocking a service, blocking a shop setting itself up.

4. **A table's name is read live in reports**, so a rename rewrites what its history is filed
   under. Argued in 10.8 rather than deferred — the alternative names a table nobody recognises.

5. **`Pos.Seed --restaurant` is still the only way into restaurant mode with data.** There is no
   onboarding endpoint by decision, so a real restaurant is set up by an operator running the
   seeder and then editing through the API.

Also **deliberate, not a gap**: everything under *Non-goals* above, and the fact that nothing in
the kitchen is audited — `AuditAction` is a short list of the actions that move money, and firing
is not one.
