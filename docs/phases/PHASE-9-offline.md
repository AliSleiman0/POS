# Phase 9 — Offline (PWA)

**Goal:** the register keeps selling when the internet drops, and the sales land correctly when it comes back.

**Why this comes after the MVP:** per `DECISIONS.md`, building the product and the sync layer simultaneously is how both end up half-finished. Offline is also the hardest correctness problem in the system, and it is much easier to reason about against flows that are already proven online.

**Depends on:** Phases 0–8. Critically on **3.5 (idempotency)** — without it this phase is a rewrite of the write path rather than an addition.

---

## 9.1 Service worker + app-shell cache

`vite-plugin-pwa` with Workbox.

- Precache the app shell (JS, CSS, fonts, icons) so the register loads with no network
- Web app manifest; installable, launches full-screen on a tablet
- **Explicit update flow.** A silent service-worker update that swaps code mid-sale is unacceptable; prompt, and apply on an idle cart only.
- Never cache API responses by default — stale prices are wrong prices. Data caching is 9.2's job, deliberately and per-resource.

**Exit criteria**
- [x] App loads with the network disabled — shell precached; asserted against the built `sw.js` in `pwa.spec.ts`
- [x] Installable; runs full-screen — manifest asserted in `pwa.spec.ts`
- [x] Update prompts rather than swapping silently — `registerType: 'prompt'`, `skipWaiting` only in the message handler
- [x] Updates never apply mid-sale — `isTillIdle` gates on empty cart **and** no minted sale key **and** no unresolved payment
- [x] No accidental API response caching — asserted on the emitted worker, and falsified by adding a `StaleWhileRevalidate` over `/api/v1/products`

## 9.2 Local catalog mirror

IndexedDB (via `idb`) in `src/lib/offline/`.

- Mirror products, barcodes, prices, tax classes and tenant settings
- Sync on login and periodically while online; incremental by `UpdatedAt` rather than a full re-download
- **Barcode lookup hits IndexedDB first**, always. This also makes scanning faster while online — the hot path stops depending on a round trip.
- Show the mirror's age in the UI. "Prices as of 09:14" lets staff judge whether to trust a price; a silent stale cache does not.
- On reconnect, refresh and surface changed prices for items sat in an open cart

**Exit criteria**
- [x] Scanning works fully offline — mirror-first always, server only as the fallback
- [x] Incremental sync, not full re-download — `GET /catalog/sync?since=`, watermark committed only after a full walk
- [x] Mirror age visible — "Prices as of 14 min ago" in the header chip
- [ ] **Not done.** Price changes on reconnect are not surfaced for a product sitting in an open cart. The mirror updates; nothing tells the cashier. See *What is not done*.
- [~] A large catalog syncs in cursor-sized pages yielding between them, and search is bounded and index-backed — but **10k products has not actually been tried**. The design is right; the measurement is missing.

## 9.3 Outbox queue

`src/lib/offline/outbox.ts`

- A completed sale is written to IndexedDB **before** any network attempt, with its `clientTransactionId`
- A background sync (or a foreground loop when the SW can't) replays queued sales against the **same** `POST /sales`, with their original `Idempotency-Key`
- Retry with exponential backoff; a `4xx` that is not a timeout is a permanent failure and moves to a review queue rather than retrying forever
- **Sync state visible at all times**: online/offline, N sales pending, last successful sync. A cashier must never wonder whether a sale saved — that uncertainty is worse than a visible failure.
- Cash-drawer totals computed from local data while offline, so a shift can still be closed
- Queue survives reload and app restart

**This is why 3.5 exists.** Because idempotency is already the contract, the outbox needs no bulk-upload endpoint, no separate sync API and no second server-side code path — it replays ordinary requests. A second write path would be a second place for the pricing rules to be wrong.

**Exit criteria**
- [x] Sales complete offline and persist locally
- [x] Replay creates each sale exactly once — asserted end to end in `offline.spec.ts`. The *partial* replay case is covered by idempotency rather than by its own test; see *What is not done*.
- [x] Queue survives reload and restart (IndexedDB, keyed per tenant). A browser **close** also keeps the queue — but the session dies with it, so the till cannot send until somebody signs in again
- [x] Sync state always visible — online/offline, N to send, N needing review, mirror age
- [x] Permanent failures reach a review queue — the classifier's two exceptions (408/429 transient, 409 reused key permanent) are tested
- [ ] **Not done.** Closing a drawer offline is not implemented. See *What is not done*.

## 9.4 Conflict reconciliation

The genuinely hard part, and the one `DECISIONS.md` flagged.

- **Duplicate submission** is already solved by idempotency. That was the easy half.
- **Oversell** — two registers both sell the last unit while offline. Both sales are valid: the goods left the shop. Stock goes negative and a `StockDiscrepancy` is raised **for staff review**. Never a silent correction, per `DECISIONS.md`: a POS that quietly adjusts stock counts destroys the only inventory data the owner has.
- **Price changed while offline** — the queued sale keeps its snapshotted price (Phase 3.3). The customer was charged what the receipt said; that is the correct and defensible outcome. Flag it if the delta is material.
- **Product deactivated while offline** — the sale still lands. History references products permanently and the product was never deleted (Phase 2.2), so nothing breaks.
- **Deleted/reset browser storage** — sales are lost. This is unrecoverable and must be stated plainly (9.5), not glossed.
- Reconciliation surfaces through the existing `GET /stock/discrepancies` plus a review UI: what happened, when, which registers, what the counts imply.

**Exit criteria**
- [x] Oversell needs no new code — `SaleWriter` already raises a `StockDiscrepancy`; covered by `SaleCommitTests`. Not re-proved through two offline browsers; see *What is not done*.
- [x] Review UI explains each refusal, its consequence for the money, and the one thing to do about it
- [x] Queued sales keep their prices — the stored body is replayed byte for byte, and `SaleLine` snapshots server-side
- [x] Sales of a since-deactivated product still land — history references products permanently
- [~] `offline.spec.ts` covers the offline window for the sale path. The oversell case is covered server-side rather than through two offline clients.

## 9.5 Honest limits

Documented in `README`/`docs`, **and surfaced in the app**:

- Browser storage is **evictable**. Safari is strictest; iOS may clear it under storage pressure or after periods of non-use. Request persistent storage (`navigator.storage.persist()`) and show whether it was granted.
- Offline is therefore **best effort on web**. Extended offline trading on a browser risks data loss, and a shop should know that before it relies on it.
- Full offline reliability arrives with the Avalonia desktop app and a real local database (Phase 12) — as `DECISIONS.md` anticipated.
- Warn when the queue grows beyond a threshold, or when the app has been offline unusually long. Do not let a shop discover the limits after losing a day's takings.

**Overstating this would be the most damaging thing in the project.** A shop that believes offline is bulletproof, trades all day on it, and loses the data will not come back — and will tell people. Underpromising costs a feature bullet; overpromising costs the business.

**Exit criteria**
- [x] Persistent storage requested on sign-in; the **answer** is surfaced — granted, denied or unsupported
- [x] `OfflineLimitsPanel` on `/sync` says what can be lost and how
- [x] `describeRisk` — refused persistence immediately, 4h offline, 20 queued; null otherwise, so the warning stays readable
- [x] Asserted: a test rejects the words safe, secure and guaranteed in every warning the app can produce

---

## Verification

Offline testing is manual by nature — devtools' offline toggle does not reproduce a flaky connection, which is the real failure mode.

1. Load the register, then disable the network
2. Scan several items (from the mirror) and complete a cash sale — confirm it succeeds and shows as pending
3. Complete three more; reload the page while offline; confirm all four are still queued
4. Re-enable the network; confirm exactly four sales land, with correct totals
5. **Drop the connection halfway through the replay**, then restore it; confirm no duplicates
6. With two browsers offline simultaneously, sell the same last unit in both; reconnect; confirm both sales land and a discrepancy is raised
7. Throttle to a slow, lossy connection (not fully offline) and repeat a sale — the ambiguous middle state is where sync bugs actually live
8. Clear site data and confirm the loss is reported honestly rather than failing silently

Automated: Playwright with `context.setOffline(true)` for the deterministic paths; integration tests for the server-side reconciliation.

## What is not done

**Written down rather than quietly left off the list.** The phase is built and green, and these four things are not in it. None is a stub or a half-implementation — each is absent, and each would be its own piece of work.

1. **Closing a drawer offline** (9.3). A cashier cannot close a shift while the till cannot reach the server. The design is settled and small — `CloseShiftRequest` carries only `countedCash`, a *typed* number, so it queues like any other write and the server computes expected cash and variance at replay — but it is not built. Consequence: an offline shift stays open until the connection returns. Nothing is lost; a shop trading offline across a shift change cannot reconcile until it is back.

2. **Surfacing a price change on reconnect for a product in an open cart** (9.2). The mirror updates on sync, and a cart already holding that product keeps the price it was scanned at. That is the *correct* outcome — the customer is being charged what they were told — but the phase doc asks for it to be **surfaced**, and it is not. A cashier is not told the shelf price moved under them.

3. **A measured large-catalog sync** (9.2). The sync pages with a yield between pages and search is bounded and index-backed, so the design answers "without stalling the UI". Nobody has run it against 10,000 products. The design is right; the measurement is missing, and a design is not a measurement.

4. **The oversell case proved through two offline clients** (9.4). The server behaviour is genuinely covered — `SaleWriter` raises a `StockDiscrepancy` on a negative on-hand and `SaleCommitTests` asserts it — but nobody has driven two browsers offline, sold the same last unit in each, and watched both land. The manual script below step 6 is the way to do it and it has not been done.

Also not done, and deliberately: **offline PIN login**. A till restarted while offline cannot sign in, because the refresh token dies with the tab (invariant 11) and a PIN is verified by the server. Caching PIN hashes locally would remove server-side lockout and rate limiting, which is a larger security change than the capability is worth. The limits panel says so in the app.

## Note

If Phase 9 proves harder than expected — and it may — the honest fallback is: ship the MVP online-only, make the offline **limits** clear, and let the desktop app (Phase 12) carry real offline durability. That is a defensible product position. A half-working sync layer that occasionally loses sales is not.

**It did prove hard, in a place the doc did not anticipate**, and the fallback was not needed. The unanticipated part was that an offline till cannot price a cart at all: every total came from `POST /sales/quote`, and invariant 3 forbade computing one on the client. That is settled by porting `Pos.Core.Pricing` to TypeScript and pinning the two engines to a shared 913-cart corpus that both test suites assert against — an explicit amendment to invariant 3, recorded in `DECISIONS.md`, rather than a drift past it. The rest of the phase rests on that being trustworthy.
