# Session Handoff

**Written:** 2026-08-02 · **Branch:** `phase-5/web-register` (not pushed, not committed) · **Phase 5.1 and 5.2 done — start 5.3**

> A cashier can open the drawer, scan items with a wedge scanner, build a cart the server prices, and drive the whole screen from the keyboard. **854 .NET · 101 Vitest · 18 Playwright**, all green locally. **CI has not run on this branch** — nothing is pushed. See [Before anything else](#before-anything-else).
>
> The open defect the last session flagged — a network blip signing the till out — **is fixed and verified in a browser.**

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**The work is uncommitted on `phase-5/web-register`.** `git status` lists 14 modified and 7 new files. Nothing has been committed, pushed, or seen by CI, so "green" above means "green on this machine". The roadmap's own rule is that a phase is not done until CI says so — commit, push, and let the four jobs run before building on 5.3.

Two things to expect from CI specifically:

- `format:check` and `lint` are separate jobs from `test`. Both pass locally (`pnpm exec prettier --check`, `pnpm exec oxlint`).
- **One unexplained Vitest timeout was seen once**, on a machine that was simultaneously running the API, the dev server and a Playwright browser; it did not reproduce in five subsequent runs and the failing test's name was lost. The three async waits in `AuthProvider.test.tsx` were given 5s instead of testing-library's 1s default as a result, since they are the ones that wait on a fetch plus an effect. If CI produces a timeout, that is the first place to look — and this time, write down which test it was.
- The `contract` job regenerates `schema.d.ts` from the running API. **No backend file changed this session**, so there is nothing for it to find — but that also means it has never run against this branch.

---

## Read first

1. [`docs/phases/PHASE-5-web-register.md`](phases/PHASE-5-web-register.md) — §5.3 is next, and §5.2 now records two mistakes worth not repeating.
2. [`CLAUDE.md`](../CLAUDE.md) — invariants 3 (money), 6 (idempotency), 7 (policies) and 10 (no blocking dialogs) are all load-bearing in what 5.3 adds.
3. [`docs/API.md`](API.md) → **Sales**. 5.3 is the milestone where `unitPriceOverride` and `discountAmount` stop being `null`, and the server **refuses** rather than ignores them without the policy.

## What landed

**The auth fix (the last session's "fix this first").** `AuthProvider` cleared the tokens on *any* `/auth/me` failure, so an unreachable API during session restore threw away a good refresh token. Now only a 401 ends the session; anything else is the new `unreachable` status, which `RequireAuth` renders as "Could not reach the shop's server" with a **Try again**, keeping the route tree mounted exactly as `expired` does. `retry: false` came off the `me` query so the shared policy in `queryClient.ts` retries a transport failure twice first. `AuthProvider.test.tsx` covers all three outcomes; both "keeps the token" cases were red before the fix.

**5.1, the register**, at `/register` inside `AppLayout` (`src/features/register/`):

- `cart.ts` — the reducer, pure and React-free. Holds products, quantities and the selection; **no money** beyond an integer minor-unit unit price for the provisional subtotal. `toSaleLines()` is the single function both the quote and (in 5.4) the sale will build their lines through.
- `CartProvider` is mounted in `AppLayout`, above the router's outlet. That is deliberate and load-bearing: the cart survives a route change *and* a session expiring, because `RequireAuth` renders the re-auth prompt over this tree rather than navigating. An e2e test walks to Overview and back to prove it.
- `queries.ts` — `useCurrentShift` (also used by the header indicator now, one query key for both), `useOpenShift`, and `useQuote`, keyed on a **cart signature** so moving the cursor does not re-price.
- `OpenShiftPanel` — on the screen, not in a menu. Idempotency key minted once per panel, as `StockAdjustmentDialog` does.

**5.2, the scanner** — `src/lib/scanner.ts` (pure) plus `useScanner` (the `document` listener) and `beep.ts`.

**Layout change worth knowing about:** `AppLayout`'s `<main>` no longer carries `p-6`. Every page brings its own padding now, so the register can run edge to edge without the layout knowing which route it is rendering. Six pages were touched for this; if a new page looks cramped against the viewport edge, that is why.

## Things that will bite you

New, and all of them cost time this session:

1. **A per-gap timing threshold splits a barcode in half.** The frame rendering the previous scan runs while the next one arrives; a 100ms stall mid-burst is normal. The scanner now budgets the *whole* burst (`maxIntervalMs` × length). If you touch the timing, the Vitest case "survives one stalled frame in the middle of a code" is the one that fails.
2. **A scan that fails the pacing test falls through to the keypad**, and `5099999000011` as a quantity clamped to 9,999. Manual entries above `MAX_LINE_QUANTITY` are now refused outright. Any new keypad path needs the same guard.
3. **`page.keyboard` cannot simulate a scanner while the page is busy.** Every CDP keystroke waits on the renderer's main thread, so a burst that follows a scan is delivered over hundreds of milliseconds — real hardware is not. `machineBurst()` in `e2e/register.spec.ts` dispatches a burst in one JS turn for the two tests that need device timing; everything else uses the real keyboard, which is what proves the listener is wired to real input at all.
4. **A test can pass for the wrong reason here.** "Typing into the search does not fill the cart" passed with the stand-down rule *removed* — the typing was slow enough to fail the pacing test anyway. Found by the falsification pass, and the test now dispatches at machine speed into the focused field. Falsify anything you add to this file.
5. **`vi.stubGlobal('fetch', …)` does not reach the generated client.** `createClient` captures `globalThis.fetch` when `api/client.ts` is first evaluated, which is import time. `src/test/fetchMock.ts` installs one swappable function up front — **import it first**, before anything that pulls in the client, or the test quietly hits the real network and fails with a `TypeError` that looks exactly like the offline case.
6. **`+` and `−` are reserved keys** in `useScanner` and never reach the buffer. Reserving anything a barcode could contain would delete that character from the middle of a code.
7. **The quote is keyed on `cartSignature`**, not the cart object. Anything added to a cart line that changes the price must go into the signature or the total will not refresh.

Carried forward, all still true — Playwright/`webServer` ordering, `playwright install --with-deps` hanging on Windows, `schema.d.ts` drift, `JsonIgnore` fields arriving as `undefined`, `.tsx` files that export hooks, the `http` profile, and the whole backend list: see the Phase 4 handoff in `git log` (`docs/HANDOFF.md` at `de7183c`) — nothing there has been invalidated.

## Verified, and not

**Verified in a real browser** (headless Chromium at 1280×900 and 834×1112, screenshots reviewed by eye): the empty register with the open-drawer prompt, opening a drawer, a three-line cart including 0.35 kg of cheese entered on the keypad, the unknown-code banner, and the overview. The €7.10 on screen is the server's quote, and it adds up.

**Verified end to end** by 7 new Playwright specs against a real API and a real Postgres: scan → line → server total, double-fire → one unit, the stand-down rule, the unknown-code banner, keyboard-only operation, the two-step cart void, and the cart surviving a route change.

**Falsified deliberately**, both restored afterwards: removing the double-fire guard makes the cart charge €2.40 for one bottle; removing the stand-down rule puts a line in the cart from typing in the search box.

**Not verified:**

- **The beep has never been heard.** Headless Chromium has no audio device, and every failure in `beep.ts` is swallowed by design. Somebody has to open the page with speakers on. The mute toggle and its `localStorage` round trip are equally unproven.
- **The re-auth overlay over a populated cart** — still. `guards.test.tsx` proves the route stays mounted and an e2e test proves the cart survives a route change, which together are the mechanism; nobody has watched a token expire with six items on screen.
- **Touch.** Targets are sized for a finger (44px+) and it was screenshotted at tablet dimensions, but no tablet has been touched.
- **The header at tablet width is cramped** — the shop name truncates to "Corne…" and the user's name wraps. Cosmetic, pre-existing, and visible in the tablet screenshot.
- **Nobody who has worked a till has used any of it.** Unchanged, and it is still the real bar the phase doc sets.

## Outstanding / deferred

- **5.3 is next** — line discount, price override, manager override. The API refuses a discount without `CanApplyDiscount` (403, not ignored), so the gate is real; `IfPolicy` handles the control side.
- **`Take cash` is present and disabled**, with a caption saying tendering arrives in the next milestone. It is not wired to anything.
- **No `sessionStorage` persistence for the cart yet** — 5.5. `CartProvider` is where it goes, and the shape was chosen so it is an addition rather than a move.
- **The dev device token was rotated this session.** The one in a browser from before is dead; re-enrol from `/settings/device`, or re-run the seeder with `--rotate-device-token`.
- **`pos_e2e` keeps its shift open between runs** — there is no close-shift UI until 6.3, so `ensureDrawerOpen()` in the register spec opens one only if there is none.
- Everything else from the Phase 4 list is unchanged: no audit log until 7.2, `GET`/`PUT /settings` unbuilt, `/sales/{id}/receipt` and `/shifts/{id}/report` documented and unbuilt, `?from=`/`?to=` on `GET /sales` not implemented, percentage discounts do not exist, `Tender.Method` accepts `Cash` only, `RebuildOnHand` has no route, `limit=abc` returns a bare 400, `auth/policies.ts` is hand-written and can drift.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing until Phase 10, but must be settled before quoting a price.
