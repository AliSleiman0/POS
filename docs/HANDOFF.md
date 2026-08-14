# Session Handoff

**Written:** 2026-08-11 · **Updated:** 2026-08-14 · **Branch:** `main` (merged, pushed, CI green) ·
**Phases 8 and 9 shipped and deployed**

> A till now sells with the network off. The sale is priced locally by an engine pinned to the
> server's, queued before any network attempt, and lands **exactly once** when the connection
> returns — dated when the customer paid rather than when it synced. That is asserted end to
> end against a real API and a real Postgres, not described.
>
> **Four things are not done.** They are listed in
> [`PHASE-9-offline.md` § What is not done](phases/PHASE-9-offline.md#what-is-not-done) and
> repeated below. None is a stub; each is absent.

> This file is session state, not durable truth. Overwrite it when you finish. Durable
> decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in
> [`ROADMAP.md`](ROADMAP.md), operational procedure in [`RUNBOOK.md`](RUNBOOK.md).

---

## Before anything else

1. **Rotate the deployment credentials.** The database owner URL and both Render deploy hooks
   were pasted into a chat transcript on 2026-08-14 to complete the deploy. Reset the database
   password (pos-db → Reset password) and regenerate both deploy hooks, then update the
   matching GitHub secrets. The app connects as `pos_app`, so resetting the **owner** password
   does not disturb the running service.
2. **The deployed database is deleted on 2026-09-10.** Free plan, 30 days, no automatic
   backups. Unchanged by this phase, and now closer.
3. **`pnpm build` before `pnpm test:e2e`**, or five service-worker specs skip. CI does this;
   locally it is on you. They throw rather than skip under `CI`.

## Deployment state as of 2026-08-14

Everything the Phase 8 handoff left owing is now done. Recorded here because the next session
will otherwise re-derive it.

| | |
|---|---|
| `main` | Phases 8 + 9 merged, CI green on all four jobs |
| Migrations on the deployed DB | `OfflineSaleTimestamps` and `CatalogSync` applied and verified |
| GitHub secrets | All five set (`DATABASE_OWNER_URL`, both deploy hooks, both origins) |
| Render `pos-api` / `pos-web` | Both switched from `phase-8/deployment` to **`main`**, both **auto-deploy Off** |
| Deploys | Triggered by hook after the migrations, in that order |

**Two defects were found by shipping, and both are fixed on `main`:**

- **`deploy.yml` had never worked.** Phase 8.3 shipped it with "the pipeline has not run for
  real", and the first real run died on `NETSDK1004`: it ran `dotnet tool restore` (which
  installs `dotnet-ef`) but never `dotnet restore` (which restores packages), and
  `migrations script` builds the startup project. Reproduced against a clean clone and fixed.
  **Expect more of the same** — the pipeline still has not completed end to end.
- **`SSH.NET` 2025.1.0 advisory** (GHSA-q939-rpr3-3284) arriving transitively via
  Testcontainers. `NuGetAudit` is at `low` with warnings-as-errors, so restore failed outright
  on a branch that was green locally. Testcontainers 4.14.0 resolves SSH.NET 2026.0.0.

**Two traps worth carrying forward**, both of which cost time:

- **Render settings do not save on selection.** Choosing a value from a dropdown does nothing
  until the **Save changes** button below it is clicked. An auto-deploy change looked applied,
  read back as "Off", and had not saved — only the browser's "Leave site?" dialog revealed it.
  Reload and re-read after every change.
- **`SELECT count(*)` as the owner returns 0 on tenant tables.** `FORCE ROW LEVEL SECURITY`
  applies to the owner, and with no `app.tenant_id` set the policy matches nothing. It reads
  as an empty table. Lift `FORCE` for the statement and restore it, exactly as the migration
  does.

## What Phase 9 actually does

- **Sells offline.** Scan from the mirror, price with the ported engine, tender, take cash. The
  panel says *"Saved on this till"* and shows a local reference — there is no sale number,
  because `SaleSequence` is assigned inside the server's transaction.
- **Lands exactly once.** The queued sale replays as an ordinary `POST /sales` with its
  original `Idempotency-Key`. No bulk endpoint, no sync API, no second server write path.
- **Keeps the right date.** `occurredAt` is minted once when the sale completes and replayed
  unchanged; `Sale.RecordedAt` records the server's clock beside it.
- **Explains itself.** Header chip: online/offline, *n* to send, *n* needing review, and the
  mirror's age. `/sync` explains every refusal and what it means for the money.
- **Does not overpromise.** The limits panel is written to be unflattering, and a test rejects
  the words *safe*, *secure* and *guaranteed* in every warning the app can produce.

## What is NOT done

1. **Offline shift close.** A drawer cannot be closed while the till is offline. The design is
   settled and small (`CloseShiftRequest` carries only a typed `countedCash`, so it queues like
   any other write) — it is simply not built.
2. **Price-change-on-reconnect for an open cart.** The mirror updates; nothing tells the
   cashier the shelf price moved under a product already in the basket. The *charge* is
   correct — the customer pays what they were told — but the phase doc asks for it to be
   surfaced.
3. **A measured 10k-product sync.** The design answers it (cursor pages, a yield between them,
   bounded index-backed search). Nobody has run it. A design is not a measurement.
4. **Oversell through two offline browsers.** The server behaviour is genuinely covered by
   `SaleCommitTests`; nobody has driven two clients offline and sold the same last unit in
   both. Step 6 of the phase doc's manual script is how.

Also deliberate, not a gap: **no offline PIN login**. See `DECISIONS.md`.

## The three decisions this phase made

All three are in [`DECISIONS.md`](../DECISIONS.md) under *Resolved 2026-08-11*. Read them
before changing any of this.

1. **`occurredAt` on `POST /sales`.** The only client-supplied value in the system that reaches
   a stored column, bounded in both directions, with `RecordedAt` beside it.
2. **Invariant 3 amended.** Two pricing engines, pinned by
   `tests/fixtures/pricing-conformance.json` — 913 carts, asserted from C# *and* TypeScript,
   compared **as strings so the decimal scale is pinned too**. A change to either engine that
   is not a change to both turns one side red.
3. **Invariant 11 reconciled.** Cart and credential stay in `sessionStorage`; the outbox is
   durable in IndexedDB, because a queued sale is money that already changed hands.

## Things that will bite you

1. **`node_modules/.vite` goes stale when a dependency is added**, and the symptom is every
   component dying with *"Invalid hook call"* in code you did not touch — `ToastProvider`, in
   this case. It looks exactly like a duplicate-React bug and it is not. `rm -rf
   node_modules/.vite`. CI never sees it. Recorded in `vite.config.ts`.
2. **`occurredAt` is part of the idempotency fingerprint.** Anything that rebuilds a queued
   request instead of replaying the stored body will re-read the clock, send a different body
   under the same key, and be answered `409 idempotency-key-reused` **for ever** — which at a
   till reads as a sale that will not go through. There is a test whose entire point is that
   two builds of the same cart differ.
3. **The pricing port's rounding is asymmetric on purpose.** Division rounds **half to even**
   (that is what .NET's `decimal` does when it runs out of room); the pipeline rounds **half
   away from zero**. Both were read off the real type by probing it. Do not "tidy" them into
   agreement.
4. **A decimal's *scale* is part of the result.** `12.97` and `12.9700` are the same number and
   not the same result — scale propagates through the multiplication that follows, and the
   pipeline rounds only at the end. The corpus compares strings for this reason.
5. **The catalog feed may repeat a row and cannot skip one.** Its sort key is
   `COALESCE(updated_at, created_at)`, which moves when a row is edited. The mirror's writes
   must stay upserts.
6. **A migration's `UPDATE` matches zero rows under `FORCE ROW LEVEL SECURITY`** when it runs
   as the owner with no `app.tenant_id` set — silently. Invisible locally, because `pos` is a
   superuser with `BYPASSRLS`. Verified against a `NOSUPERUSER NOBYPASSRLS` owner: `UPDATE 0`
   versus `UPDATE 2`.
7. **The pending-sales badge is hidden before the provider has counted.** A test that waits for
   it to disappear passes instantly on a fresh page. Poll the server instead — three e2e drafts
   died on this.
8. **Still true from before:** `pnpm format:check` is its own CI step; never run `dotnet test`
   and Playwright at once; a leftover `dotnet run` holds :5013; `InvariantGlobalization` must
   stay `false`; origin values are a matched set.

## Where the offline code lives

| | |
|---|---|
| `src/Pos.Web/src/lib/pricing/` | The ported engine. `decimal.ts` is the load-bearing file |
| `src/Pos.Web/src/lib/offline/` | `db`, `catalog`, `sync`, `outbox`, `replay`, `connectivity`, `persist` |
| `src/Pos.Web/src/features/offline/` | Provider, status chip, queued-sale panel, review page, limits |
| `tests/fixtures/pricing-conformance.json` | The oracle. Regenerate with `POS_REGENERATE_PRICING_CORPUS=1` — it rewrites and then **fails**, deliberately |
| `src/Pos.Web/e2e/offline.spec.ts` | The five specs that prove the phase |

## Test counts

1441 .NET · 321 Vitest · 70 Playwright. All green locally. **Not yet run in CI** — the branch
is unpushed.

## Longer-standing gaps this phase did not change

- **No password change or reset flow.** Still the largest gap for a shipped product, still on
  no phase's list.
- A deactivated user's access token works for ~15 minutes.
- `/reports/sales-summary` and `/reports/top-products` documented, not built.
- Margin cost is not snapshotted.
- The beep (unverified since 5.2), a real thermal printer, and whether the audit log answers
  the question somebody actually asks.
- **Nobody who has worked a till has used any of it.** No phase gate clears this. It was the
  real bar before Phase 9 and it is the real bar now — and Phase 9 has made it more urgent
  rather than less, because offline behaviour is exactly the kind of thing that reads fine in a
  test and wrong at a counter.
