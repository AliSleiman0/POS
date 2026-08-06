# Session Handoff

**Written:** 2026-08-06 · **Branch:** `phase-5/reload-recovery` · **Phase 5 is done — start 6.1**

> A reload no longer costs a basket, and a reload *during a payment* no longer risks charging the
> customer twice. The till asks the server what its key bought instead of guessing.
> **877 .NET · 159 Vitest · 35 Playwright**, all green locally.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**Nothing is committed.** The branch is `phase-5/reload-recovery`, off `0c97db1` (the 5.4 merge).
There is **one API change** this time — a new endpoint — so `pnpm generate:api` has been run and
`src/api/schema.d.ts` is part of the diff. The client-drift CI job finally has something to check.

**No migration.** The lookup rides an index that already existed
(`ux_sale_tenant_client_transaction_id`).

**PR #13 was merged** with `gh pr merge 13 --merge` — no `--admin` needed, so branch protection was
never gating on the starved jobs. See the CI note below.

## Read first

1. [`docs/phases/PHASE-5-web-register.md`](phases/PHASE-5-web-register.md) → §5.5, which records
   four decisions and the 5.4 hole this milestone closed.
2. [`DECISIONS.md`](../DECISIONS.md) → the two new Phase 5.5 entries. The first says why recovery is
   a **read** and not a re-POST, and that reasoning is load-bearing: it is the difference between a
   safe page load and one that charges people.

## What landed

**`GET /sales/by-client-transaction/{id}`** — the whole backend half. Reuses `ReadAsync`, so a
recovered sale arrives with its lines, tenders and `changeGiven` and the completion panel renders
straight from it. A key that reached the server but bought nothing (a refused under-tender still
writes an idempotency record) is a 404, because the question is about the sale.

**The cart is persisted on every change** (`features/register/storage.ts`, wired in `CartProvider`
by lazy `useReducer` init). Versioned, structurally validated on read, and every call wrapped —
malformed storage must not stop a till from opening, because reloading is the only remedy anyone on
a shop floor has.

**Recovery has three outcomes, not two** (`useSaleRecovery.ts`). Taken → the completion panel with
`provenance: 'recovered'`. Not taken → the tender pad restored with its amounts. **Unreachable → a
banner saying so and telling the cashier not to re-ring it**, with the record kept so the question
can be asked again.

**The 5.4 dead end is closed.** `IdempotencyFilter` fingerprints the request *body*, so the same key
with an edited basket is `409 idempotency-key-reused` — not a replay. The till now looks up what the
key bought, names that sale, dismisses the tender pad and gives the basket a fresh identity
(`restartSale`). It previously said "try again", which would have failed identically for ever.

## Things that will bite you

New this session:

1. **`ServerDecimal` is `number | string`, and a validator that forgets it fails silently.** The
   first version of `storage.ts` required a string for `unitPrice`, so every persisted cart was
   rejected on read — and a rejected cart is indistinguishable from an empty till, so the feature
   would have looked like it worked and done nothing. Caught only because the round-trip test
   asserted the line came back. **Any structural validator against these types needs the same
   care.**
2. **A re-POST is not a safe way to ask a question.** It is safe when the sale landed and it
   *creates the sale* when it did not. This is written up in `DECISIONS.md` because it is the kind
   of shortcut that looks obviously fine.
3. **`resolveSpentKey` deliberately dismisses the tender pad.** Money has already changed hands for
   a different basket, so this is a stop-and-check, not a press-again. An e2e test asserts the pad
   is gone — if you "fix" that, read the test's comment first.
4. **One Playwright flake seen, once.** `register › …sees the server price it` expected `€2.40` and
   read `€1.20` in a full-suite run; it passed 3/3 alone and on every subsequent full run. The
   mechanism is `useQuote`'s `placeholderData`, which keeps the previous total on screen — so a
   slow quote under parallel load shows a stale figure for longer than the 5s expect. **Not
   diagnosed further, and not fixed.** If it recurs, that is where to look.
5. **A leftover `dotnet run` locks the build.** `dotnet build` failed with MSB3027 on
   `Pos.Data.dll` held by a `Pos.Api` process from the previous session. `Get-Process Pos.Api |
   Stop-Process -Force`.

Carried forward and still true: the 5.4 list (the emptied-cart stale total, serial mode for the
sale-counting block, `GET /stock/{productId}/movements` not `/stock/movements`, Playwright's
`webServer` refusing a held :5013, the Chrome extension's broken screenshot API), the 5.3 list, and
the Phase 4 backlog at `de7183c`.

## Verified, and not

**Falsified deliberately, restored** — three separate mechanisms, each confirmed red:

- the in-flight record write removed → the reload test comes back to an ordinary empty till;
- the reused-key branch removed → the edited-basket test never sees the recovered panel;
- `CartProvider`'s lazy init removed → the basket does not survive a remount.

(5.4's own falsification — minting the key inside the mutation — still stands from that milestone.)

**Verified end to end** by 4 new Playwright specs: a reload keeping the basket, a reload after the
payment landed, a reload after it never arrived, and editing the basket after a lost response. Plus
6 Vitest cases for the recovery hook, including the unreachable branch asserting that **neither**
callback fires.

**Not verified:**

- **The beep. Still.** Outstanding since 5.2, now four sessions. Headless has no audio device. It
  needs a person with speakers on, and there are two places it fires.
- **The unreachable banner in a real browser.** Its logic is covered by Vitest, but nobody has seen
  it rendered. Devtools offline on `/register` with a marker in `sessionStorage` would do it.
- **Nobody who has worked a till has used any of it.** Unchanged, and still the real bar.

## CI

Actions was not creating runs at all for a while yesterday — a push produced no run whatsoever, and
before that `e2e` and `API contract` recorded `cancelled` with zero steps after 15 minutes queued
while the other two jobs passed on the same commit. **Check `gh run list` before assuming this
branch's CI means anything**, and remember that a run whose jobs were cancelled reports
`conclusion: failure` at the *run* level. github.com/settings/billing is the first place to look.

## Outstanding / deferred

- **The receipt action** — `GET /sales/{id}/receipt` is documented and unbuilt. It is the one Phase
  5 exit criterion deliberately left undone, and 6.1 is where it gets built; the completion panel
  says so rather than stubbing it.
- **The tender pad has no "clear all"** for a mis-keyed split — each row has an ×, which is enough
  for two entries and would not be for six.
- Everything else from the 5.3 list stands: no percentage discounts, no audit of a refused override
  until 7.2, `LineAdjustDialog` labelling its field with the currency *code*, and the unidentified
  GitGuardian finding on PR #12 (it has not recurred — #13 passed that check).

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing
until Phase 10, but must be settled before quoting a price.
