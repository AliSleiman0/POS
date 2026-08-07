# Session Handoff

**Written:** 2026-08-07 · **Branch:** `phase-6/receipt-model` · **Phase 6 is done — start 7.1**

> A customer leaves with a receipt, and an owner can see what was sold and whether the drawer
> balanced. **954 .NET · 181 Vitest · 48 Playwright**, all green locally.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions
> belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

---

## Before anything else

**Four commits on `phase-6/receipt-model`, off `1f3551a` (the Phase 5 merge). Nothing is pushed
and no PR is open.** The branch name says 6.1 because that is where it started; it carries all
four milestones. Push it, or split it, before starting 7.1.

**There is one migration** — `20260807162021_ReceiptSettings`, four nullable columns on `tenant`.
Additive, no destructive change, already applied to `pos_dev` and applied to `pos_e2e` by
Playwright's own webServer command.

**There are API changes in every milestone**, so `pnpm generate:api` has been run and
`src/api/schema.d.ts` is committed. The CI client-drift job has something real to check.

## Read first

1. [`DECISIONS.md`](../DECISIONS.md) → the three new **Phase 6.1** entries. Two are load-bearing:
   why `InvariantGlobalization` is now off, and why reprints are marked by the client with the
   limitation that leaves.
2. [`docs/phases/PHASE-6-receipts-reporting.md`](phases/PHASE-6-receipts-reporting.md) → every
   exit criterion is ticked with a note on *how*, including the three defects found on the way.
3. [`docs/API.md`](API.md) → receipts, the two report routes, the `/sales` history contract, and
   the two report routes that are **deferred** rather than missing.

## What landed

**6.1 — the receipt model.** `Pos.Core/Receipts`, pure, plus `GET /sales/{id}/receipt`. Tax is
broken down by rate with the residue placed deliberately so the parts sum to `TaxTotal` exactly.
`Tenant` gained four nullable receipt columns.

**6.2 — browser printing.** `features/sales/Receipt.tsx` renders the server's payload and decides
nothing. The preview *is* the printed element — one render through a portal outside `#root`, so a
preview cannot drift from the paper. 80mm roll, A4 fallback, reprints marked. The completion
panel's receipt button — Phase 5's outstanding debt — is built.

**6.3 — the Z-report.** `/shifts/{id}/report` and `/reports/daily`, same shape, one
`ReportScope`, plus Owner-only `/reports/margins`. `BusinessDay` is pure and survives both clock
changes. A closed shift's variance is **read**, never recomputed; an open one is computed live
and labelled `isProvisional`. Screens for both, under a new `CanCloseShift` nav group.

**6.4 — sale history.** List, detail and an in-page refund dialog. Filters compose, date bounds
are trading days, and the refund link works **both ways**. The history pages newest first.

## Things that will bite you

New this session:

1. **`InvariantGlobalization` was `true` from Phase 0 and is now `false`.** Under it Windows
   cannot resolve an IANA time zone at all while Linux still can — so receipts and trading-day
   reports would have passed on the runner and failed on every developer machine. Invariant 8 has
   depended on ICU since it was written; the build was not honouring it. **If a build or a
   container image changes, do not let this get set back.**
2. **EF's `SqlQuery<T>` maps columns by *snake_case*, not by the alias you write.** With
   `UseSnakeCaseNamingConvention` on, `AS "ChangeGiven"` fails with "the required column
   'change_given' was not present" — while single-word aliases pass, because the lookup is
   case-insensitive and only the underscore defeats it. Every alias in `ReportQueries` is
   snake_case for that reason. It does map multi-column rows onto records, which is why the
   reports are not raw ADO.NET.
3. **The e2e database accumulates and that has now broken two things.** `pos_e2e` is seeded and
   never dropped. `saleCount` read one page of 100 and saturated at the hundredth sale; the stock
   screen's row fell off page one at the fiftieth product. **Any e2e assertion that counts, or
   that expects a row on the first page, has a shelf life.**
4. **Playwright now runs one worker locally as well as in CI.** Every spec trades in the same
   shop through the same drawer. This also retires the 5.6 flake — it was parallel load.
5. **Signing in twice without signing out does nothing.** `/login` sits outside the auth guard
   and bounces an authenticated visitor to `/`, so the form is never used and the old identity
   survives. It looks exactly like a session bug. `reports.spec.ts` has a `switchTo` helper.
   (The app is fine: adopting a session refetches `/auth/me` before returning.)
6. **A loaded machine fails this e2e suite.** Two runs went red on different specs with 1 GB of
   16 GB free; both passed on a quiet machine. Check memory before believing a red e2e run.

Carried forward and still true: a leftover `dotnet run` locks the build (`Get-Process Pos.Api |
Stop-Process -Force`), Playwright's `webServer` refuses a held :5013, and the Phase 4 backlog.

## Verified, and not

**Falsified deliberately, restored** — six mechanisms, each confirmed red:

- the receipt's tax residue step removed → red on random basket 0 in both tax modes, **while
  every hand-written example still passed**;
- `BusinessDay.Of` reduced to "convert, subtract, take the date" → red on both clock changes;
- the `/stock` search predicate removed → all three new search tests red;
- the refund dialog's key minted per render → the two attempts carry different keys;
- the descending keyset predicate left pointing forwards → the page walk loses four of seven;
- `noBlockingDialogs` extended to `features/sales` → red on a planted `confirm()`.

**Looked at in a browser**, which is the only reason three defects were found: the 80mm roll
stretching to the full page width, report table headings rendering as `NetTax` over two number
columns, and the sales history opening on the shop's first ever sale.

**Not verified:**

- **The beep. Still.** Outstanding since 5.2, now five sessions. Headless has no audio device.
- **A real printer.** Print output is checked under Chromium's print emulation, which honours the
  stylesheet but not a thermal printer's own margins. Nobody has put paper through one.
- **Nobody who has worked a till has used any of it.** Unchanged, and still the real bar.

## Fixed on the way, outside Phase 6's scope

- **`GET /stock` ignored `?q=`.** The web client has sent it since Phase 4.3 and the handler never
  declared the parameter, so the stock screen's search box has never filtered — it returned an
  unfiltered first page, which looked right while the shop had fewer products than a page. Fixed
  by sharing `/products`' predicate; three tests go red without it.
- **A shift was scoped by the day it opened**, so an overnight drawer's takings appeared on
  today's report with no float behind them and no shift to say the drawer was uncounted. It read
  as fully reconciled.

## CI

**Not yet run for this branch** — nothing is pushed. All four jobs were green on `main` at
`1f3551a`. The drift job matters here: every milestone changed the API.

A run whose jobs were *cancelled* reports `conclusion: failure` at the **run** level — inspect the
jobs before believing a red run. Branch protection does not gate on these checks.

## Outstanding / deferred

- **Server-side reprint counting.** Reprints are client-marked today. Closing it properly needs an
  append-only receipt-issue record, which is Phase 7.2's audit-log shape — the reasoning and the
  limitation are both in `DECISIONS.md`. **A client that chose not to send the mark would print an
  unmarked duplicate**, and §6.2 is right that this is a refund-fraud vector.
- **Margin cost is not snapshotted.** `SaleLine` records no cost price, so `/reports/margins`
  restates itself when a supplier's price changes. Fixing it is a schema change and a decision,
  not a query. Stated in `docs/API.md` rather than hidden.
- **`/reports/sales-summary` and `/reports/top-products`** are documented and deferred; nothing
  has a screen for them.
- **No `PUT /settings`.** The four receipt fields and the cash-rounding increment are reachable
  only through `tools/Pos.Seed` (`--receipt-footer`, `--cash-rounding`).
- **The tender pad still has no "clear all"**, and everything else from the 5.3 list stands.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing
until Phase 10, but must be settled before quoting a price.
