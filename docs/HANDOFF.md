# Session Handoff

**Written:** 2026-08-05 · **Branch:** `phase-5/cash-payment` · **Phase 5.4 done — start 5.5**

> A cart becomes a sale. Tender entry, quick cash, split tender, change due off the server's own
> figure, and **exactly one sale however many times Complete is pressed.**
> **871 .NET · 138 Vitest · 31 Playwright**, all green locally.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**Nothing is committed.** The branch is `phase-5/cash-payment`, off `fb69129`. No migration and no
API change this time, so the client-drift job has nothing to catch.

**Your dev tenant now has cash rounding.** This session ran
`dotnet run --project tools/Pos.Seed -- --cash-rounding 0.05`, which is a **new option** and the
only one that changes an existing tenant. Set it back to `0` if you want the old behaviour.

## Read first

1. [`docs/phases/PHASE-5-web-register.md`](phases/PHASE-5-web-register.md) — §5.4 records two
   defects found by driving it, and §5.5 says what is left of it.
2. [`DECISIONS.md`](../DECISIONS.md) → the sale-GUID entry. Where the key lives is load-bearing for
   5.5 and is not an arbitrary choice.

## What landed

**The tender pad** (`TenderPanel.tsx`) replaces the total in the right-hand column rather than
covering the screen, so the cart stays visible — the moment a customer says "actually, take that
off" is while they are reaching for their wallet.

- Quick cash: Exact, the next whole unit, then the notes above the total. Integer minor units
  throughout (`tender.ts`), and none of it decides what anyone pays: it suggests what a person
  might hand over.
- Split tender: several amounts with a running balance, sent as one `tenders` array.
- `SaleCompletePanel.tsx` shows change due at `text-6xl` — the **server's** `changeGiven`, not the
  provisional figure the pad was showing while the cashier counted.

**The idempotency key lifecycle, pulled forward from 5.5.** `cart.saleKey` is minted by a
`beginSale` action when tendering starts, is idempotent so backing out and returning is still one
sale, and dies with `clear`. `unwrapWithResponse` in `api/client.ts` finally gives `wasReplayed()`
a caller, so a retry says "already recorded" instead of "recorded".

**`--cash-rounding` on the seeder**, because there is no `PUT /settings` and without it the
rounding line was unreachable from a browser.

## Things that will bite you

New this session:

1. **An emptied cart inherited the last customer's total, and had since 5.1.** `useQuote` keeps the
   previous answer on screen so a scan does not blank the biggest number on the till; the cost is
   that a cart with nothing in it shows the price of the cart before it. Before 5.4 it took a cart
   void to notice — a completed sale makes it happen every time, and it put "€14.15" directly under
   the change due for an empty basket. `RegisterPage` now passes
   `quote={isEmpty(cart) ? undefined : quote.data}`. An e2e assertion pins it.
2. **`fullyParallel: true`, so counting rows in a shared tenant races.** The cash-payment describe
   is `mode: 'serial'` for exactly this reason — it is the only block in the suite that *writes
   sales*, and "exactly one sale" is a claim you can only make by counting. Everything above it can
   stay parallel.
3. **A double click is not a test of double-submit safety.** The button disables itself while a
   request is in flight, so a double click proves the courtesy and says nothing about the
   mechanism. The real test lets the first request reach the server (`route.fetch()`) and then
   aborts the response, so the till sees a failure the server never had. Falsified: mint the key
   inside `useCompleteSale` and it goes red.
4. **`GET /stock/movements` does not exist** — it is `GET /stock/{productId}/movements`, and the
   list is oldest-first, so `items[0]` is the seeded `Receive`. The stock test compares on-hand
   before and after instead, which is the actual claim anyway.
5. **Playwright's `webServer` will not start if a dev API is already on :5013.** Kill
   `Pos.Api` before `pnpm test:e2e`, or the run dies with "already used".
6. **The Chrome extension's screenshot API broke mid-session** with a CDP
   `params.clip.scale` deserialisation error and did not recover across resize or re-navigation.
   Fell back to a scripted headless Chromium, which is what Phase 5.1 did for the same reason.
   Do not sink time into it — write the script.

Carried forward and still true: the 5.3 list (modal stand-down for the scanner, the grant beside
the cart not in it, the union rule in `authorize()`, `cartSignature` completeness, `form_input` not
reaching React), the 5.1/5.2 list, and the Phase 4 backlog at `de7183c`.

## Verified, and not

**Verified in a real browser** (headless Chromium at 1280×900, screenshots reviewed): a two-item
cart tendered by split payment with the balance counting to zero; change due €25.00 against €39.15
taken on €14.15; the cart clearing to a **€0.00** total; and a `--cash-rounding 0.05` tenant showing
a €0.17 line priced to €0.15 with "Includes −€0.02 cash rounding" spelled out.

**Verified end to end** by 7 new Playwright specs: exact cash, server-computed change, split
tender, **a lost response not charging twice**, the stock decrement, a cashier's manager-approved
discount going all the way to a sale, and backing out of the tender step keeping the same sale.

**Falsified deliberately, restored:** minting the key inside the mutation — the retry then creates
a second sale and the replay banner never appears.

**Not verified:**

- **The beep, still.** Outstanding since 5.2 and now three sessions old. Headless has no audio
  device. It needs a person with speakers on, and the completion path adds a second place it fires.
- **Tablet width for anything since 5.1.** `resize_window` did not take in either browser session.
  The tender pad is in the same column the total was, so it should inherit the working layout —
  but "should" is doing the work in that sentence.
- **A grant expiring mid-sale.** The five-minute window is handled (`override-required` on submit
  clears the grant and asks for the PIN again) and the branch has never run: it needs a cashier to
  be slow on purpose. The .NET side has an expiry test.
- **Nobody who has worked a till has used any of it.** Unchanged, and still the real bar.

## Outstanding / deferred

- **5.5 is what is left of double-submit safety** — persisting the in-flight sale to
  `sessionStorage` so a *reload* recovers. `CartProvider` is where it goes, the cart already
  carries the GUID, and the grant is deliberately outside the cart so it will not be serialised.
- **No receipt action** — 6.1 builds the payload; the completion panel says so.
- **The tender pad has no "clear all" for a mis-keyed split** — each row has an ×, which is enough
  for two entries and would not be for six.
- Everything else from the 5.3 list stands: no percentage discounts, no audit of a refused
  override until 7.2, `LineAdjustDialog` labelling its field with the currency *code*, and the
  unidentified GitGuardian finding on PR #12.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing
until Phase 10, but must be settled before quoting a price.
