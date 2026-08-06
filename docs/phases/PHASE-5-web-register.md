# Phase 5 — Web: Register Screen

**Goal:** a cashier can scan items, build a cart, take cash and complete a sale — fast enough to use with a queue waiting.

**This is the screen that decides whether the product is adopted.** Everything else can be adequate; the register cannot. Staff use it hundreds of times a day, and if it is two seconds slower than what they had before, they will say so.

**Depends on:** Phases 3 and 4.

---

## 5.1 Register layout

`src/features/register/`

Three regions: **cart** (left, the focus), **keypad + total + tender** (right), **product grid** (searchable, for items without barcodes — fresh produce, bakery, anything unlabelled).

- Touch targets large enough for a finger on a counter tablet
- Total displayed prominently — it is read across a counter, sometimes by the customer
- **Fully keyboard-operable.** Scanners *are* keyboards, and experienced staff never touch the screen for common actions. A mouse-only flow fails at a real till.
- Shift state visible; opening a shift is the first action of the day and must be obvious rather than buried in a menu

**Exit criteria**
- [x] Layout works at tablet and desktop widths — two columns at `lg`, stacked below; screenshotted at 1280×900 and 834×1112
- [x] Every action reachable by keyboard — ↑/↓ select, `+`/`−` step, digits then Enter set a quantity, `Delete` voids, `F2` focuses the search. Printable characters belong to the scan/keypad buffer, so the shortcuts are keys a barcode cannot contain
- [x] Total legible from a metre away — `text-5xl`, and it is the server's figure from `POST /sales/quote`, not a client sum
- [x] Shift state visible; open-shift prompt when none is open — in the right-hand column where the tender panel will be, not in a menu

> **Pulled forward from 5.3:** the quote round trip and keypad quantity entry. A "total displayed prominently" that was a client-side guess would have been the wrong foundation. Line discount, price override and the manager-override flow stay in 5.3.

## 5.2 Scan input

`src/lib/scanner.ts`

A barcode wedge scanner types the code and presses Enter — it is indistinguishable from a very fast human. So:

- A **global** keystroke handler that works with **no field focused**, because staff will not click into an input first
- Must not fight manual entry: while a text input has focus, the handler stands down
- Distinguish scanner from human by inter-keystroke timing (a scanner emits characters in a few milliseconds), then treat the buffered string as a code on Enter
- Debounce duplicate scans within a short window — scanners double-fire, and that becomes two units sold
- Unknown barcode (`404`) → a non-blocking prompt ("unknown item — search, or add it?"), never a modal error that stalls the queue. **No `alert()`.**
- Audible/visual feedback on a successful scan. Staff do not look at the screen between items; without feedback they cannot tell a miss from a hit.

**Exit criteria**
- [x] Scan works with nothing focused — a `keydown` listener on `document`, active whenever the session is live
- [x] Manual entry unaffected while focused — `isEditableTarget` stands the handler down for input/textarea/select/contenteditable. Pinned by an e2e test that dispatches a machine-paced burst at the focused field; typing alone did **not** pin it (see below)
- [x] Duplicate double-fire suppressed, with a test — 300ms from the previous scan's *start*, so transmission time does not eat the window
- [x] Unknown code is non-blocking — an in-page banner offering "search for it", no dialog, no `alert()`
- [x] Feedback on every scan, success or failure — a synthesised beep (high on a hit, low on a miss) plus a flash on the affected line, with a mute toggle

**Two things this milestone got wrong first, both found by driving it rather than by reasoning:**

1. **Per-gap timing splits codes.** The first implementation started a new buffer whenever two keystrokes were more than 60ms apart. The frame that renders the *previous* scan runs exactly when the next one arrives, so a real scan lost its leading digit and the register looked up `099999000011` — a code nobody scanned, which could match another product. The rule is now a budget over the whole burst: `maxIntervalMs` per character *on average*. One stalled frame is survivable; a person typing the same string is an order of magnitude over.
2. **A mis-timed scan became a quantity.** When a burst failed the pacing test it fell through to the keypad, and `5099999000011` clamped to the maximum quantity — 9,999 bottles of water. A manual entry above `MAX_LINE_QUANTITY` is now refused outright with "that looked like a barcode, not a quantity", and nothing changes.

## 5.3 Cart interactions

- Quantity change (keypad and +/−), including decimal quantities for weighed goods
- Line void, cart void (cart void confirms — but with an in-page confirm, **never** `window.confirm`, which blocks the page)
- Line discount and price override, **gated on `CanApplyDiscount` / `CanOverridePrice`**. A Cashier sees no such control, and the API rejects it regardless. A manager override flow (manager PIN authorises a single action without swapping the session) is the usable version of this.
- Every priced change re-quotes via `POST /sales/quote`

**The client never computes the total.** It displays what the server computed. A second pricing implementation in TypeScript will disagree with the C# one — over rounding, tax-inclusive extraction, or discount apportionment — and the disagreement surfaces as a customer being charged an amount that doesn't match the receipt.

Where a running subtotal must appear before the quote returns, it is computed in **integer minor units** and clearly non-authoritative. `0.1 + 0.2 !== 0.3` in JavaScript, and that is not acceptable on a till display.

**Exit criteria**
- [x] Qty, void, discount, override all work — line discount, cart discount and price override, each re-quoting through `POST /sales/quote`
- [x] Decimal quantities for `Kilogram`/`Litre` products — unchanged from 5.1, and re-pinned after the cart signature grew three fields
- [x] **No control applies a discount on the caller's own authority without the policy**, and the API refuses the cart if forced (`403 override-required`) — see the re-wording below
- [x] Manager override authorises one action without a session swap — `POST /auth/override`, single-use, consumed inside the sale's transaction
- [x] No `alert()`, `confirm()` or `prompt()` anywhere in the register — pinned by `noBlockingDialogs.test.ts` rather than by review
- [x] Displayed total always matches the server's quote — the quote now enforces the same policies, so the till cannot show a figure the sale would refuse

> **One exit criterion was re-worded rather than ticked as written.** It said "discount/override
> controls **absent** without the policy". Taken literally a Cashier would have no way to *start*
> the manager-override flow, which is the flow's entire purpose — the control has to be reachable
> for a manager to be called over to it. What is built instead: a Cashier has **no control that
> discounts on their own authority.** Pressing Discount goes to the manager's PIN first, every
> time, and if the manager declines or cannot authorise it, nothing changes and the amount field
> is never reached. An e2e test asserts exactly that. The server refuses the cart regardless, so
> the UI path is the courteous route to the same answer rather than the enforcement.

**What this milestone decided, worth not relitigating:**

1. **The manager override needed a backend half, and there was no third option.** `POST /sales`
   takes the cashier from the token, so authorising with the manager's *session* attributes the
   sale to them and reconciles the wrong drawer. The grant is what lets the approval and the
   attribution be different people. Rationale and the two rejected alternatives are in
   [`DECISIONS.md`](../../DECISIONS.md).
2. **The grant is single-use, not short-lived.** An expiry alone would let one PIN discount every
   sale in its window — the exact fraud the flow exists to prevent.
3. **The quote enforces the policies too.** It did not before, so a client could have shown a
   discounted total the sale would refuse: the cashier reads it out, the customer counts out the
   money, and only then does it come back `403`. The quote validates a grant without spending it,
   because the register re-quotes on every keystroke.
4. **The grant lives beside the cart, never in it.** 5.5 adds `sessionStorage` persistence to
   `CartProvider`; a grant in the cart reducer would be written to a shared tablet's disk as a
   silent side effect of that. A Vitest case fails if it ever reaches storage.

## 5.4 Cash payment

- Tender screen: amount entry, quick-cash buttons (exact, next round note, common denominations)
- **Change due displayed large** — this is the number the cashier reads out loud
- Split tender: multiple cash entries, running remaining balance
- On completion: change due, and a receipt action (print / skip / reprint later)
- Cash rounding adjustment shown explicitly when it applies, so a total that isn't the sum of the lines is explained rather than looking like a bug

**Exit criteria**
- [x] Tender, change, quick-cash all correct — quick cash is Exact, the next whole unit, then the notes above the total, computed in integer minor units
- [x] Split tender with running balance — several amounts, one request, the balance counting down to zero
- [x] Change due is the most prominent element on the screen — `text-6xl`, larger than the total ever was, and it is the **server's** `changeGiven`
- [x] Rounding adjustment shown when non-zero — and *explained*, not merely shown; seed with `--cash-rounding 0.05` to see it
- [x] Completion returns to a clean cart, ready for the next customer with no extra click — the next scan is what clears the change panel
- [ ] ~~A receipt action (print / skip / reprint later)~~ — **deferred to 6.1.** `GET /sales/{id}/receipt` is documented and unbuilt, so there is nothing to print. The completion panel says so rather than stubbing it, the same treatment `Take cash` had before this milestone built it.

> **Pulled forward from 5.5: the idempotency key lifecycle.** The key is minted when *tendering
> begins* and reused on every attempt. Building the submit the other way first would have shipped a
> double-charge on any retry — `api/idempotency.ts` says so in its own doc comment — and then fixed
> it a milestone later. `sessionStorage` persistence and reload-recovery remain 5.5.

**Two things this milestone found by being driven rather than reasoned about:**

1. **An emptied cart inherited the last customer's total.** `useQuote` keeps the previous answer on
   screen so a scan does not blank the biggest number on the till; the cost is that a cart with
   nothing in it showed the price of the cart before it. A bug since 5.1, where it took a cart void
   to see — a completed sale makes it happen every time, and it put "€14.15" directly under the
   change due for an empty basket. A stale total that looks authoritative is the worst failure this
   screen has.
2. **A split tender listed its amounts without labels.** "Cash taken €10.00" followed by two bare
   rows, on the panel a cashier checks when the drawer does not balance.

## 5.5 Double-submit safety

> **Half of this landed in 5.4.** The key lifecycle — minted when tendering begins, reused on every
> attempt, replay surfaced to the cashier — is done and pinned by an e2e test that lets the first
> request commit and throws its response away. What remains here is persistence: surviving a
> *reload*, which needs the cart and its GUID in `sessionStorage`.

- Generate the `clientTransactionId` GUID **when the sale begins**, not when submit is pressed. Every retry — including a page reload mid-submit — reuses it. ✅ *done in 5.4, except across a reload*
- Persist the in-flight sale (GUID + cart) to `sessionStorage` before submitting, so a crash or reload recovers rather than losing the sale
- On timeout or network error: retry with the **same** key. The server returns the original result if the first attempt actually landed.
- Optimistic UI, but the cart is not cleared until the server confirms

**A disabled submit button is not the mechanism.** It does not survive a reload, a flaky connection, or a double-tap that registers before React re-renders. The idempotency key is the mechanism; the disabled button is a courtesy.

**Exit criteria**
- [ ] Rapid double-click produces exactly one sale
- [ ] Reload mid-submit recovers and does not duplicate
- [ ] Retry after a simulated timeout returns the original sale
- [ ] Cart survives a failed submit and can be retried

## 5.6 Tests

- **Vitest** — scanner timing/debounce, cart reducer, tender arithmetic display, idempotency key lifecycle
- **Playwright** — open shift → scan (simulated fast keystrokes) → adjust quantity → tender cash → verify change → confirm the sale persisted and stock decremented → reprint the receipt
- **Playwright** — double-submit: intercept the first request, delay it, click again, assert one sale
- **Playwright** — a Cashier cannot reach discount or override controls

**Exit criteria**
- [ ] All of the above green in CI
- [ ] The double-submit test would fail if idempotency were removed (verify by temporarily removing it)

---

## Verification

```powershell
docker compose up -d
dotnet run --project src/Pos.Api
pnpm --dir src/Pos.Web dev
```

**Full manual smoke** (the plan's Phase 5 gate, driveable with the Chrome MCP tools):

1. Log in as Owner, open a shift with a float
2. Scan a product (a USB scanner, or paste-and-Enter to simulate) — appears in the cart
3. Change a quantity; add a weighed item at 0.350
4. Apply a line discount as Manager; confirm a Cashier cannot
5. Tender cash over the total; verify change due
6. Complete; verify the receipt, the persisted sale and the stock decrement
7. Reload mid-submit and confirm no duplicate sale
8. Close the shift; verify the variance

**Then hand it to someone who has worked a till and watch them use it without instructions.** Every real usability problem in a POS is found this way and not in a test suite.
