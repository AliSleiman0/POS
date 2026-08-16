# Session Handoff

**Written:** 2026-08-15 · **Branch:** `phase-10/restaurant-ui` (committed, **not pushed, not merged**) ·
**Phase 10 is complete. A person can work a restaurant.**

> Seat a table, order with a required question answered, fire the round, watch it land on the
> grill's screen, bump it, take a plate off with a reason, split the bill, settle it with a tip,
> and see the table go free — **in a browser**, and asserted as one Playwright walk-through. Paying
> still writes an ordinary `Sale` through `ISaleWriter`, with the same number counter, stock ledger
> and Z-report a counter sale uses.
>
> **Every exit criterion in the phase doc is ticked.** What is left is in its *What is not done*,
> and the largest item is not code.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md),
> operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

1. **Push the branch and let CI run it.** `phase-10/restaurant-ui` has three commits and has never
   been to CI. Everything below is green locally. **Merging to `main` auto-deploys to production**
   — CI success on `main` triggers `deploy.yml`, which migrates and deploys both services.
2. **Still owed from Phase 8:** rotate the deployment credentials. The database owner URL and both
   Render deploy hooks were pasted into a chat transcript on 2026-08-14, and the 10.4 deploy used
   all three, so they are demonstrably live.
3. **The production database self-deletes on 2026-09-10.** Free plan, no automatic backups.
4. **`pnpm build` before `pnpm test:e2e`**, or five service-worker specs skip.

## Where it got to

| Milestone | State |
|---|---|
| 10.0 – 10.6 | ✅ Done (previous sessions) |
| 10.7 Restaurant UI | ✅ **Done this session** |
| 10.8 Restaurant reporting | ✅ **Done this session** |
| 10.9 e2e + seed fixture | ✅ **Done this session** |
| The four recorded loose ends | ✅ **All four closed** |

**Tests: 1779 .NET · 324 Vitest · 72 Playwright.** All green locally. `dotnet build` and
`pnpm build` warning-free; `pnpm lint` and `format:check` clean.

## What this session built

**`features/restaurant/`** — `FloorPage`, `OrderPage`, `ModifierSheet`, `VoidLineDialog`,
`BillPage`, `KitchenPage`, plus `queries.ts`, `serviceMode.ts` and `idempotency.ts`.

**The kitchen display is a kiosk route outside `AppLayout`** — no nav, no header, nothing that is
not a ticket. A pass is an appliance bolted to a wall, not a page somebody navigates to.

**`/register` picks the screen from `serviceMode`.** One route, not two: staff are trained on "the
register", and a second URL per mode would mean every deep link and the PIN screen's return path
had to know the mode. `RegisterPage` is *wrapped, never edited* — it is a thousand lines with
paying users on it.

**10.8 reporting** and **`restaurant.spec.ts`**, which walks the phase doc's nine-step verification.

## Five things the work turned up, all fixed

1. **`CanVoidFiredLine` was not grantable**, so a waiter on a shared handheld could not have a
   manager authorise a cancelled plate — they would have had to hand the device over to sign in,
   and what actually happens then is a supervisor session left open all evening, attributing every
   later void to somebody who was not there. Added to the allow-list with its own argument, six
   tests, and `ARCHITECTURE.md` now states the admission test: **bounded, audited, one transaction
   in front of the approver**. `CanRefund` stays off it.
2. **`POST .../bills/{id}/pay` returned no change due.** It would have needed a second call at the
   moment a waiter is counting notes into a hand. `ChangeGiven` now comes back on the pay response,
   out of the commit that computed it. Null on an unpaid bill, because zero means "tendered
   exactly".
3. **The restaurant never offered to open a drawer.** A retail till shows `OpenShiftPanel` on the
   register; a floor is not a till, so a shop could seat, order and fire all evening and discover
   at the first bill that no drawer existed — with no control anywhere to open one. **The e2e spec
   found this**, which is the argument for writing it in the same phase.
4. **Bill allocation was check-then-act.** Two waiters splitting one table could over-allocate a
   line. Now under the same `FOR UPDATE` on the order row that line numbering and firing take, and
   proved with two real clients racing.
5. **A barcoded modifier resolved at the scanner** — 10.3's last unticked box, and a real gap
   rather than an oversight in the list.

## Things that will bite you

1. **The tender pad's provisional change ignores the tip.** `TenderPanel` computes its running
   balance from the total alone, so a tipped bill reads high by the tip until the server answers.
   Every figure on it is labelled provisional and the settled panel shows the server's number, but
   **a cashier counting notes reads the pad**. Left alone because the panel is shared with the
   retail till and the change is to a money path with users on it. **Fix this before a real shop
   uses it.**
2. **`ServerDecimal` is `number | string` and the generated client means it.** Every numeric field
   — including `int` counts like `covers` and `course` — comes through as the union. Narrow once
   with `parseServerDecimal`; a `>` against the raw value does not compile and `Number()` on its
   own accepts nonsense.
3. **`api/problem.ts` lists error slugs exhaustively** so a `switch` over them is checked, and
   **`auth/policies.ts` does the same for policies**. Both were missing every Phase 10 entry, which
   is why `RequirePolicy policy="CanWorkKitchen"` would not compile. Add to both when the server
   grows one.
4. **`noBlockingDialogs.test.ts` scans by glob.** A new feature folder is outside it until the glob
   is extended — do that in the commit that creates the folder, not after.
5. **A hook that reads `useOffline` cannot live in `AppLayout`**, which is what renders
   `OfflineProvider`. That is why the nav is its own component.
6. **The e2e restaurant tenant is `e2e-restaurant`, a second shop.** Flipping the shared one would
   break every retail spec. The restaurant spec is **one test**, because a test per screen depends
   on the last one's leftovers and `pos_e2e` outlives the run.
7. **Still true:** a leftover `dotnet run` holds :5013 *and* locks the build's DLLs; never run
   `dotnet test` and Playwright at once; `pnpm format:check` is its own CI step.

## What to do next

**A real shop.** `DECISIONS.md` has argued for it since Phase 8 and nothing since has weakened the
argument. The floor, the modifier sheet and the pass are guesses about how a room works until
somebody who works one disagrees with them — and that is now the only untested claim that matters.

Before that, in order: **fix the tip/provisional-change gap** (item 1 above), **rotate the
credentials**, and **deal with the database expiring on 2026-09-10**.

The phase doc's *What is not done* also lists the missing admin screens for stations, modifier
groups and the floor. Not blocking a service; blocking a shop setting itself up without an
operator running `Pos.Seed`.
