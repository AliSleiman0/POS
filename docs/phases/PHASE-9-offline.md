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
- [ ] App loads with the network disabled
- [ ] Installable; runs full-screen
- [ ] Update prompts rather than swapping silently
- [ ] Updates never apply mid-sale
- [ ] No accidental API response caching (verify in devtools)

## 9.2 Local catalog mirror

IndexedDB (via `idb`) in `src/lib/offline/`.

- Mirror products, barcodes, prices, tax classes and tenant settings
- Sync on login and periodically while online; incremental by `UpdatedAt` rather than a full re-download
- **Barcode lookup hits IndexedDB first**, always. This also makes scanning faster while online — the hot path stops depending on a round trip.
- Show the mirror's age in the UI. "Prices as of 09:14" lets staff judge whether to trust a price; a silent stale cache does not.
- On reconnect, refresh and surface changed prices for items sat in an open cart

**Exit criteria**
- [ ] Scanning works fully offline
- [ ] Incremental sync, not full re-download
- [ ] Mirror age visible
- [ ] Price changes on reconnect are surfaced, not silently applied to a cart mid-sale
- [ ] A large catalog (10k+ products) syncs and searches without stalling the UI

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
- [ ] Sales complete offline and persist locally
- [ ] Replay on reconnect creates each sale exactly once — verified by replaying a queue that partially succeeded before the connection dropped again
- [ ] Queue survives reload, restart and browser close
- [ ] Sync state always visible
- [ ] Permanent failures reach a review queue, not an infinite retry
- [ ] Shift close works offline

## 9.4 Conflict reconciliation

The genuinely hard part, and the one `DECISIONS.md` flagged.

- **Duplicate submission** is already solved by idempotency. That was the easy half.
- **Oversell** — two registers both sell the last unit while offline. Both sales are valid: the goods left the shop. Stock goes negative and a `StockDiscrepancy` is raised **for staff review**. Never a silent correction, per `DECISIONS.md`: a POS that quietly adjusts stock counts destroys the only inventory data the owner has.
- **Price changed while offline** — the queued sale keeps its snapshotted price (Phase 3.3). The customer was charged what the receipt said; that is the correct and defensible outcome. Flag it if the delta is material.
- **Product deactivated while offline** — the sale still lands. History references products permanently and the product was never deleted (Phase 2.2), so nothing breaks.
- **Deleted/reset browser storage** — sales are lost. This is unrecoverable and must be stated plainly (9.5), not glossed.
- Reconciliation surfaces through the existing `GET /stock/discrepancies` plus a review UI: what happened, when, which registers, what the counts imply.

**Exit criteria**
- [ ] Concurrent offline oversell → both sales land, stock negative, discrepancy raised
- [ ] Review UI explains the discrepancy well enough to act on
- [ ] Queued sales keep their snapshotted prices
- [ ] Sales of a since-deactivated product still land
- [ ] Each of the above has an integration test simulating the offline window

## 9.5 Honest limits

Documented in `README`/`docs`, **and surfaced in the app**:

- Browser storage is **evictable**. Safari is strictest; iOS may clear it under storage pressure or after periods of non-use. Request persistent storage (`navigator.storage.persist()`) and show whether it was granted.
- Offline is therefore **best effort on web**. Extended offline trading on a browser risks data loss, and a shop should know that before it relies on it.
- Full offline reliability arrives with the Avalonia desktop app and a real local database (Phase 12) — as `DECISIONS.md` anticipated.
- Warn when the queue grows beyond a threshold, or when the app has been offline unusually long. Do not let a shop discover the limits after losing a day's takings.

**Overstating this would be the most damaging thing in the project.** A shop that believes offline is bulletproof, trades all day on it, and loses the data will not come back — and will tell people. Underpromising costs a feature bullet; overpromising costs the business.

**Exit criteria**
- [ ] Persistent storage requested; grant status visible
- [ ] Limits documented in user-facing help, not only in code comments
- [ ] Warning when the queue is large or the offline window is long
- [ ] No marketing or UI copy claims guaranteed offline durability on web

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

## Note

If Phase 9 proves harder than expected — and it may — the honest fallback is: ship the MVP online-only, make the offline **limits** clear, and let the desktop app (Phase 12) carry real offline durability. That is a defensible product position. A half-working sync layer that occasionally loses sales is not.
