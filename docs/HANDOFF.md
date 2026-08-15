# Session Handoff

**Written:** 2026-08-15 · **Branch:** `phase-10/restaurant-mode` (not merged, not pushed, **not committed**) ·
**Phase 10 is in progress: the server side is done, nothing has a screen**

> A restaurant can be seated, ordered for, split and settled — through the API. Paying a bill
> writes an ordinary `Sale` through `ISaleWriter`, with the same number counter, the same stock
> ledger and the same Z-report a counter sale uses. That is the phase's central claim and it is
> asserted end to end against a real Postgres.
>
> **Nothing in it has a user interface.** `/register` still renders the retail screen for every
> tenant. Every endpoint works and none of them has a caller.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md),
> operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

1. **Nothing is committed.** The whole phase is uncommitted work on
   `phase-10/restaurant-mode`. `git status` will show ~60 changed and added files. Commit it
   before doing anything else, or a stray `git checkout` loses a day.
2. **Still owed from Phase 8, and now three weeks older:** rotate the deployment credentials
   (the database owner URL and both Render deploy hooks were pasted into a chat transcript on
   2026-08-14).
3. **The deployed database self-deletes on 2026-09-10.** Free plan, 30 days, no automatic
   backups. Unchanged by this phase and now much closer.
4. **`pnpm build` before `pnpm test:e2e`**, or five service-worker specs skip.

## Where it got to

| Milestone | State |
|---|---|
| 10.0 Service mode | ✅ Done |
| 10.1 Order model | ✅ Done |
| 10.2 Orders API + floor | ✅ Done |
| 10.3 Modifiers | ✅ Done (server side; no admin screen) |
| 10.4 Kitchen | ❌ **Not built** |
| 10.5 Bills, splitting, payment | ✅ Done |
| 10.6 Tips | ✅ Done |
| 10.7 Restaurant UI | ❌ **Not built** |
| 10.8 Restaurant reporting | ❌ Not built (the Z-report's tips line shipped with 10.6) |
| 10.9 e2e + seed fixture | ❌ Not built |

**Tests: 1685 .NET · 321 Vitest.** All green locally. `dotnet build` and `pnpm build` are
warning-free, `pnpm lint` and `pnpm format:check` clean. **Nothing has run in CI** — the branch is
unpushed.

Full detail, and the five gaps written out, in
[`PHASE-10-restaurant.md` § What is not done](phases/PHASE-10-restaurant.md#what-is-not-done).

## Two real defects were found, both in shipped code

**1. `GET /catalog/sync` served an arbitrary tenant's settings.** The handler read
`db.Tenants.FirstOrDefaultAsync()` with **no `Where`**. `Tenant` is deliberately not
tenant-owned — it is the list of tenants and login resolves a row in it before any tenant is
known — so it carries no query filter, no RLS policy and no interceptor check. The scoping was one
clause the handler had to contain, and did not. Every till mirrored some other shop's currency,
tax mode, rounding increment and receipt address; **offline, `taxMode` is an input to the pricing
engine**, so the till would have priced carts by another business's rules.

It had a passing test. The isolation world seeds both tenants *identically* — deliberately, so a
leak doubles a list — which makes the two possible answers the same string in every field that
test asserted on. The first field that differed was `serviceMode`, added in 10.0. **A fixture
built to make leaks obvious by duplication hides them in any field it duplicates**; the new test
sets up two shops that disagree about everything it checks.

**2. `OfflineSaleTests.A_sale_rung_yesterday_lands_in_yesterdays_history` was date-dependent.**
It derived the query day from the **UTC** date while the filter resolves trading days in the
**tenant's** zone. Correct for twenty-three hours a day; wrong between 23:00 and midnight UTC in
summer, which is when it was run. Invariant 8's failure mode, in a test.

## Things that will bite you

1. **`ORDER` and `TABLE` are reserved words in SQL.** The tables are `customer_order` and
   `dining_table`. This codebase writes raw SQL in the money paths — EF cannot aggregate a
   value-converted `Money` — and 10.8's reporting will add more.
2. **An order line stores inputs, not amounts.** There is no `LineTotal` on `OrderLine`, unlike
   `SaleLine`, and that is deliberate: the money comes from `PricingEngine` whenever a bill is
   quoted or settled. Do not add one "for the screen" — it is a second set of numbers to keep in
   step through every edit, void and re-split.
3. **A bill's idempotency key is minted with the bill, not with the payment.** Invariant 6. A key
   per attempt makes the header decorative and charges the table twice on a retry.
4. **The tip comes out of the change, never into the total.** That is what leaves it inside
   `ExpectedCash` without `ShiftArithmetic` changing at all. Folding it into `Total` would inflate
   revenue, inflate the tax owed on revenue nobody was charged tax for, and make a refund of a
   meal offer to hand the gratuity back.
5. **Adding an `AuditAction` member needs a migration.** `HasEnumAsText` generates a check
   constraint, so the model has pending changes until you generate one — and an `AuditManifest`
   row plus the test it names, or the build fails.
6. **Every new tenant-owned table needs `ApplyTenantRowLevelSecurity()` called in *its own*
   migration.** An applied migration does not re-run.
7. **The isolation world now runs restaurant mode**, because the order routes are gated and a
   retail tenant answers `409` from all of them — a manifest row would otherwise pass against an
   endpoint that never looked at the resource.
8. **Still true from before:** `pnpm format:check` is its own CI step; never run `dotnet test` and
   Playwright at once; a leftover `dotnet run` holds :5013; `node_modules/.vite` goes stale when a
   dependency is added and presents as "Invalid hook call".

## Where the new code lives

| | |
|---|---|
| `src/Pos.Core/Entities/` | `Order`, `OrderLine`, `OrderBill`, `ServiceArea`, `DiningTable`, `ModifierGroup`, the enums; `Sale.TipAmount`, `Product.IsModifier` |
| `src/Pos.Core/Menus/ModifierRules.cs` | Pure. The questions a menu asks, enforced at the API |
| `src/Pos.Data/Orders/OrderWriter.cs` | The counter upsert and the `FOR UPDATE` on an order row |
| `src/Pos.Api/Endpoints/` | `OrderEndpoints`, `OrderBillEndpoints`, `FloorEndpoints`, `MenuEndpoints` |
| `src/Pos.Api/Orders/RestaurantModeFilter.cs` | The `409 restaurant-mode-required` gate, on the group |
| `tests/Pos.Api.Tests/Infrastructure/RestaurantTenant.cs` | The fixture every restaurant test builds on |

## What to do next

**Build 10.7 and 10.9 together, and 10.4 with them.** Not in that order — together. Per
[`ROADMAP.md`](ROADMAP.md#test-the-phase-before-starting-the-next-one), UI testing is the one that
must never accumulate, and right now there is no screen *and* no way to click through this by hand
(`Pos.Seed` has no `--restaurant` flag). The server side is finished and proved; what is missing is
everything a person touches, and shipping more API before any of it exists would be building the
second storey of a house with no stairs.

**Nobody who has worked a restaurant has seen any of this.** That was the real bar before Phase 9
and it is the real bar now.
