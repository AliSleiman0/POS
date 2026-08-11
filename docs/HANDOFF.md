# Session Handoff

**Written:** 2026-08-11 · **Branch:** `phase-9/offline` (**not pushed, not merged**) ·
**Phase 9 built and green, with four gaps recorded**

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

1. **`phase-8/deployment` is still unmerged**, and `phase-9/offline` was branched from it. The
   Phase 8 handoff's warnings still apply in full and have **not** been actioned:
   `deploy.yml` fires on the first green CI run on `main` and will fail because none of
   `DATABASE_OWNER_URL`, `RENDER_API_DEPLOY_HOOK`, `RENDER_WEB_DEPLOY_HOOK`, `API_ORIGIN` or
   `WEB_ORIGIN` exists as a GitHub secret. Set them before merging either branch.
2. **Two migrations are new and unapplied anywhere but local dev**: `OfflineSaleTimestamps`
   and `CatalogSync`. Both are hand-edited — read them before deploying, especially the
   `FORCE ROW LEVEL SECURITY` note in the first.
3. **The deployed database was due for deletion on 2026-09-10.** Free plan, 30 days, no
   automatic backups. Unchanged by this phase.
4. **`pnpm build` before `pnpm test:e2e`**, or five service-worker specs skip. CI now does this;
   locally it is on you. They throw rather than skip under `CI`.

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
