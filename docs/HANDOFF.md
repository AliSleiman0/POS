# Session Handoff

**Written:** 2026-08-15 · **Branch:** `phase-10/restaurant-mode` (committed, **not pushed, not merged**) ·
**Phase 10: the server side is finished. Nothing has a screen.**

> A shop can be seated, ordered for, **fired to the kitchen, bumped**, split and settled — every
> one of those through the API and none of them through a browser. Paying a bill writes an
> ordinary `Sale` through `ISaleWriter`, with the same number counter, the same stock ledger and
> the same Z-report a counter sale uses. That is the phase's central claim and it is asserted end
> to end against a real Postgres.
>
> **`/register` still renders the retail screen for every tenant.** Every endpoint works and none
> of them has a caller. What changed this session is that `Pos.Seed --restaurant` now builds a
> room, a kitchen and a menu, so the next session has something to build against.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md),
> operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

1. **Push the branch.** Everything is committed now (two commits: `9664b32` for 10.0–10.6,
   and this session's for 10.4 and the seeder) but **nothing has ever run in CI**. 1758 .NET and
   321 Vitest tests are green locally; that is not the same claim.
2. **Still owed from Phase 8, and now a month older:** rotate the deployment credentials — the
   database owner URL and both Render deploy hooks were pasted into a chat transcript on
   2026-08-14.
3. **The deployed database self-deletes on 2026-09-10.** Free plan, 30 days, no automatic
   backups. Unchanged by this phase and now four weeks away.
4. **`pnpm build` before `pnpm test:e2e`**, or five service-worker specs skip.

## Where it got to

| Milestone | State |
|---|---|
| 10.0 Service mode | ✅ Done |
| 10.1 Order model | ✅ Done |
| 10.2 Orders API + floor | ✅ Done |
| 10.3 Modifiers | ✅ Done, **bar one box** — see below |
| 10.4 Kitchen | ✅ **Done this session** (server side; no display screen) |
| 10.5 Bills, splitting, payment | ✅ Done |
| 10.6 Tips | ✅ Done |
| 10.7 Restaurant UI | ❌ **Not built — and now the whole of what is left** |
| 10.8 Restaurant reporting | ❌ Not built (the Z-report's tips line shipped with 10.6) |
| 10.9 e2e + seed fixture | 🔨 Seed fixture **done**; `restaurant.spec.ts` not written |

**Tests: 1758 .NET · 321 Vitest.** All green locally. `dotnet build` and `pnpm build` are
warning-free, `pnpm lint` and `pnpm format:check` clean.

Full detail and the gaps written out in
[`PHASE-10-restaurant.md` § What is not done](phases/PHASE-10-restaurant.md#what-is-not-done).

## What this session built

**10.4, the kitchen.** `Station`, `StationRouting` (pure, in `Pos.Core`), `KitchenTicket` and
`KitchenTicketLine`, `POST /orders/{id}/fire`, `GET /kitchen/tickets`, bump and recall, and
`/stations` CRUD. Migration `20260815085726_Kitchen` — three new tables and two nullable columns,
all additive, `ApplyTenantRowLevelSecurity()` called.

Four decisions worth knowing before you touch it:

- **Routing lives on the category and is inherited.** `StationRouting` walks up from the product
  and takes the first station set. A restaurant will configure eight categories and will not
  configure four hundred products; `Product.StationId` is the override for the one item that does
  not follow its neighbours.
- **Unrouted refuses the whole fire, naming the product.** Not the offending line — firing half a
  round puts the rest of the table in the kitchen with no record of what was dropped. And never a
  default station: that sends a steak to the bar silently and the first anybody knows is a
  customer asking after forty minutes.
- **Firing is idempotent by the line's status, not by the key.** The status changes in the same
  transaction that writes the ticket, so a second handheld carrying *its own* key creates nothing
  — which a stored response would not have covered. The endpoint carries a key anyway, so a
  genuine retry replays the original tickets instead of answering "nothing to fire". Proved
  concurrently in `KitchenTicketWriterTests`.
- **A ticket is append-only.** A line voided after firing is struck through *on the screen*
  (`isVoided`, read from the order line's current status) and never edited out of the ticket. The
  chef may already have plated it, and a line that vanished would erase the evidence that the shop
  lost one.

**`Pos.Seed --restaurant`.** Switches service mode, then three stations, two areas with eight
tables, a nine-item menu and two modifier groups. Changes an existing tenant, like
`--cash-rounding`. Re-runnable. The menu is shaped to exercise the routing walk: "Food" → Pass,
"Starters" and "Desserts" set nothing and inherit it, "Mains" overrides to Grill, "Drinks" → Bar.
`MENU-201 Beef Burger` asks a required question, so the refusal can be seen by hand.

**A contract change that reached the frontend.** `stationId` is now on the product and category
requests and responses, so `pnpm generate:api` was re-run. Two existing screens needed it: `PUT`
replaces, so `ProductDetailPage` sending no `stationId` would have **un-routed a product on every
price edit** — and the next fire would have refused the whole round naming an item nobody touched.
It now sends the existing value back, exactly as it already did for `isModifier`.

## Things that will bite you

1. **`ORDER` and `TABLE` are reserved words in SQL.** The tables are `customer_order` and
   `dining_table`. This codebase writes raw SQL in the money paths — EF cannot aggregate a
   value-converted `Money` — and 10.8's reporting will add more.
2. **An order line stores inputs, not amounts.** There is no `LineTotal` on `OrderLine`, unlike
   `SaleLine`, and that is deliberate: the money comes from `PricingEngine` whenever a bill is
   quoted or settled. Do not add one "for the screen". **The same rule holds for a kitchen
   ticket, one step further** — a ticket carries no prices at all, because a kitchen charges
   nobody.
3. **A bill's idempotency key is minted with the bill, not with the payment.** Invariant 6. A key
   per attempt makes the header decorative and charges the table twice on a retry.
4. **The tip comes out of the change, never into the total.** That is what leaves it inside
   `ExpectedCash` without `ShiftArithmetic` changing at all.
5. **A modifier's own `Course` and `SeatNumber` are not consulted.** A child line takes them from
   its own request and nothing normalises them to the parent's, so they can disagree; firing and
   billing both group by the parent, so today the child's values are inert. If you ever group by a
   child's own course, fix the write path first.
6. **Adding an `AuditAction` member needs a migration** — `HasEnumAsText` generates a check
   constraint — plus an `AuditManifest` row and the test it names. Nothing in the kitchen is
   audited, deliberately, so 10.4 needed none of this.
7. **Every new tenant-owned table needs `ApplyTenantRowLevelSecurity()` in *its own* migration.**
   An applied migration does not re-run.
8. **The isolation world runs restaurant mode** and now seeds a station, a fired line and a
   kitchen ticket. `AssertTenantAsOrderStillHasItsLines` expects **two** lines because of it.
9. **Still true from before:** `pnpm format:check` is its own CI step; never run `dotnet test` and
   Playwright at once; a leftover `dotnet run` holds :5013; `node_modules/.vite` goes stale when a
   dependency is added and presents as "Invalid hook call".

## Where the new code lives

| | |
|---|---|
| `src/Pos.Core/Entities/` | `Station`, `KitchenTicket`, `KitchenTicketLine`, `KitchenTicketStatus`; `Product.StationId`, `Category.StationId` |
| `src/Pos.Core/Menus/StationRouting.cs` | Pure. Product, then the category chain, nearest first. Null is a real answer |
| `src/Pos.Data/Orders/KitchenTicketWriter.cs` | The `FOR UPDATE`, the routing reads, the ticket insert |
| `src/Pos.Api/Endpoints/` | `KitchenEndpoints`, `StationEndpoints`; `FireAsync` in `OrderEndpoints` |
| `tools/Pos.Seed/RestaurantSeeder.cs` | The room, the kitchen and the menu behind `--restaurant` |
| `tests/Pos.Data.Tests/Orders/KitchenTicketWriterTests.cs` | Two handhelds firing at once |

## What to do next

**10.7 and 10.9 together — the restaurant UI and its e2e spec, as one piece of work.** Per
[`ROADMAP.md`](ROADMAP.md#test-the-phase-before-starting-the-next-one), UI testing is the one thing
that must never accumulate. The blocker the last handoff named is gone: `Pos.Seed --restaurant`
gives you a room to build against and to click through.

The surfaces, roughly in the order a service needs them: floor view → order screen (courses,
seats, modifier sheet, per-course fire) → **kitchen display** (poll at
`KitchenEndpoints.PollIntervalSeconds`, served from the server so it can be tuned without a new
build) → bill screen reusing `TenderPanel` and `SaleCompletePanel`. Order state is server-owned
through TanStack Query — a deliberate departure from `CartProvider`/`sessionStorage`, because a
table's order has to be visible from a second device. Invariant 11 is not weakened: nothing new
goes to storage.

**Nobody who has worked a restaurant has seen any of this.** That was the real bar before Phase 9,
it was the real bar in the last handoff, and it is still the real bar.
