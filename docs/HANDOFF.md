# Session Handoff

**Written:** 2026-08-16 · **Branch:** `main` — Phase 10 merged (`f3a3284`) and deploying ·
**The MVP and both post-MVP phases are done. There is no Phase 11.**

> Phases 0–10 are complete and on `main`. A retail shop sells, reconciles and survives the network
> going down; a restaurant is seated, ordered for, fired to a kitchen, bumped, split, settled with
> a tip and reported on — through screens, by a person.
>
> **Read the next section before planning anything.** The obvious next step by numbering does not
> exist, and the thing that is genuinely next is not code.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md),
> operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## There is no Phase 11 — read this first

**Phase 11 was card payments, and it was dropped on 2026-07-31.** Not deferred, not parked:
dropped. [`DECISIONS.md`](../DECISIONS.md) states it as a decision about the *product* rather than
the phase — the shop this is built for takes cash across a counter, so there is no processor to
integrate, no PCI surface and no payment hardware. `Tender.Method` stays a discriminator so a
standalone terminal would be additive, but nothing is built for it and nothing is planned.
[`ROADMAP.md`](ROADMAP.md)'s Beyond-MVP table shows it struck through.

**If you were sent here to "continue on Phase 11", that instruction was based on a stale number.**
What follows Phase 10 is below.

| Phase | Name | State |
|---|---|---|
| ~~11~~ | ~~Card payments~~ | **Dropped.** A closed question, not an open one |
| 12 | Avalonia desktop | Scoped, no phase doc. Same API, durable local DB, real offline |
| 13 | Business layer | Scoped, no phase doc. Platform admin, loyalty, purchase orders, gift cards, analytics |

Both are marked in `ROADMAP.md` as *"Scoped, not yet planned in detail. Order is a guess; revisit
after the first paying client."* Neither has been planned, and `DECISIONS.md` gates part of 13
explicitly: **no platform admin UI until after the first paying client.**

## What is actually next

**A real shop.** `DECISIONS.md` has argued this since Phase 8 and every phase since has made the
argument stronger rather than weaker. Nobody who has worked a till or a pass has used any of this.
The floor, the modifier sheet and the kitchen display are informed guesses about how a room works
until somebody who works one disagrees with them, and no amount of green tests closes that.

**One decision is now due, by the project's own terms.** `DECISIONS.md`, on the unresolved pricing
model:

> **Not blocking**: no billing or licensing code exists in the MVP, so this can stay open **until
> Phase 10**. It does need deciding before a price is quoted to a real customer, because "lifetime
> hosting for a one-time fee" is a liability that grows with every tenant.

Phase 10 closed on 2026-08-15. The question is "sell as a whole product, not a per-seat license"
(leaning one-time) against "we host it" (ongoing infra cost per tenant, forever). It is not a code
task and cannot be resolved by writing any. It blocks quoting a price, which blocks the first
paying client, which is what `ROADMAP.md` says gates the ordering of 12 and 13.

## Three operational items, in priority order

1. **Rotate the deployment credentials.** The database owner URL and both Render deploy hooks were
   pasted into a chat transcript on **2026-08-14**. Two deploys have used all three since, so they
   are demonstrably live working credentials. This has been carried in the handoff for two days and
   is the oldest open risk in the project. Procedure: [`RUNBOOK.md`](RUNBOOK.md).
2. **The production database self-deletes on 2026-09-10.** Render free plan, 30 days, **no
   automatic backups**. Twenty-five days out. Either move off the free plan or accept losing it and
   say so out loud — the current state is neither.
3. **Confirm Render auto-deploy is off** for both services. `deploy.yml`'s own header says the
   migration step is decorative if it is on. Two deploys have succeeded, which does not prove
   nothing is racing them.

## One known defect, and it is in a money path

**`TenderPanel`'s provisional change ignores the tip.** It computes its running balance from the
total alone, so during a tipped bill it reads high by the tip until the server answers. Every
figure on the panel is labelled provisional and `bill-paid` shows the server's real figure
afterwards — but **a cashier counting notes into a customer's hand reads the pad**, not the panel
that comes after.

Left alone deliberately in 10.7: the component is shared with the retail till, and the change is to
a path with paying users on it. It is small — `TenderPanel` needs the tip in its `Payable` prop and
in `remainingMinor`/`provisionalChangeMinor` — and it should be the first thing done before a real
restaurant uses this.

## What Phase 10 left behind, beyond that

From [`PHASE-10-restaurant.md` § What is not done](phases/PHASE-10-restaurant.md#what-is-not-done):

- **No admin screens for stations, modifier groups or the floor.** All reachable through the API
  and seeded by `Pos.Seed --restaurant`; a manager cannot add a table or re-route a category
  without one. Not blocking a service — blocking a shop setting itself up without an operator.
- **A table's name is read live in reports**, so a rename refiles its history. Argued rather than
  deferred: a report naming a table nobody recognises is worse.
- Phase 9's four gaps are **still open and still Phase 9's**: offline shift close,
  price-change-on-reconnect, a measured 10k-product sync, and the oversell case through two offline
  browsers.

## Where things stand mechanically

**Tests: 1779 .NET · 324 Vitest · 72 Playwright.** Green locally and green in CI on the branch
before the merge. `main` is at `f3a3284`; CI and then Deploy run automatically on it — **check that
the deploy went green before assuming production has this**.

**Deploy is automatic and gated on CI.** A green CI run on `main` triggers `deploy.yml`, which
generates an idempotent migration script, applies it as the schema owner, fires both Render hooks
and health-checks. It had never once completed before 2026-08-15 — the script step was missing a
design-time connection string, fixed in `b4b1b71`.

## Things that will bite you

1. **`ServerDecimal` is `number | string` and the generated client means it.** Every numeric field,
   including `int` counts like `covers` and `course`. Narrow once with `parseServerDecimal`; a `>`
   against the raw value does not compile and a bare `Number()` accepts nonsense.
2. **`api/problem.ts` and `auth/policies.ts` are exhaustive lists** so a `switch` over them is
   checked. Both silently lacked every Phase 10 entry until 10.7 needed them. Add to both when the
   server grows a slug or a policy.
3. **`noBlockingDialogs.test.ts` scans by glob.** A new feature folder is outside invariant 10's
   guard until the glob is extended — do it in the commit that creates the folder.
4. **A hook reading `useOffline` cannot live in `AppLayout`**, which renders `OfflineProvider`.
   That is why the nav is its own component.
5. **A leftover `dotnet run` holds :5013 *and* locks the build's DLLs.** `dotnet build` then fails
   with MSB3027 naming the process.
6. **Never run `dotnet test` and Playwright at once.** `pnpm build` before `test:e2e`, or five
   service-worker specs skip. `pnpm format:check` is its own CI step.
7. **The e2e restaurant tenant is `e2e-restaurant`, a second shop** — flipping the shared one would
   break every retail spec. `restaurant.spec.ts` is deliberately **one test**: a test per screen
   depends on the last one's leftovers, and `pos_e2e` outlives the run.

## If you are starting Phase 12 anyway

There is no phase doc. **Write one first** — every previous phase has one, and the ones that went
well had it before any code. `ROADMAP.md`'s line is *"Same API, durable local DB, real offline."*

The constraint that shapes it is already decided: the desktop app is a **client of the same
`Pos.Api`**, which is only true while `Pos.Core` and `Pos.Data` stay client-agnostic — enforced
today by the architecture test in `Pos.Core.Tests`, and the reason invariant 1 exists at all.

Phase 9's offline work is browser-shaped (IndexedDB, a service worker, a `sessionStorage` cart).
A desktop app with a real local database is a different problem, and the part worth reusing is the
**outbox contract** — a client-minted GUID, replayed under its original `Idempotency-Key` against
the same `POST /sales` — rather than any of the storage. That contract is why Phase 9 needed no
sync API and no second server-side code path, and it is the thing that makes a second client
cheap.
