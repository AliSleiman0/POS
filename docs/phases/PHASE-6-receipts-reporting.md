# Phase 6 — Receipts & Reporting

**Goal:** a customer leaves with a receipt, and at the end of the day an owner can see what was sold and whether the drawer balances.

**Depends on:** Phases 3 and 5.

---

## 6.1 Receipt model

`Pos.Core/Receipts/ReceiptBuilder.cs` + `GET /sales/{id}/receipt`.

**Rendered server-side into a structured payload**, not composed in the browser. One source of truth feeding three consumers: browser print now, a thermal printer via the desktop app later, and email/SMS later still. Three independent renderers guarantee three subtly different receipts, and the one a tax authority looks at will be the wrong one.

Contents: tenant header (name, address, tax number), sale number, timestamp in tenant timezone, cashier, register, lines (description, qty, unit price, line total), discounts, **tax breakdown by rate** (a legal requirement in most jurisdictions and not derivable from a single total), tenders and change, rounding adjustment, footer text.

Reads only snapshotted `SaleLine` data — never current catalog prices (Phase 3.3).

**Exit criteria**
- [x] Structured payload, all amounts from snapshots — `ReceiptSource` carries no product, so there is nothing to join a current price to
- [x] Tax broken down by rate, and the parts sum to `TaxTotal` — residue placed on the largest group, as `DiscountApportionment` does; pinned by a property test over 400 random mixed-rate baskets in both tax modes, and falsified (removing the residue step went red on basket 0 while every hand-written example still passed)
- [x] Timestamp in the tenant's timezone, not UTC — and this is what found `InvariantGlobalization=true`, latent since Phase 0, which made an IANA zone unresolvable on Windows. See `DECISIONS.md`.
- [x] Refund and voided-sale receipts render correctly — `ReceiptKind`, with status checked before type so a voided refund prints as a void
- [x] Configurable header/footer per tenant — `AddressLine`, `TaxNumber`, `ReceiptHeader`, `ReceiptFooter`, all nullable so a shop that has filled none of them in still prints

**Not done here:** the register's receipt *button*, which is 6.2's — the payload has to exist before there is anything to print.

## 6.2 Browser printing

`src/features/sales/Receipt.tsx` + an 80mm print stylesheet.

- `@media print` sized to 80mm roll width, monospace, no colour, no page margins fighting the roll
- Print from the completion screen and reprint from history
- Reprints are **marked as reprints**. An unmarked duplicate receipt is a refund-fraud vector.
- Also offer a plain-A4 fallback for shops printing to an office printer before they buy a thermal one

**Exit criteria**
- [x] Print preview correct at 80mm — and the preview *is* the printed element, rendered once through a portal outside `#root`, so it cannot drift from the output. Checked with real print-media pixels, which is what caught the roll stretching to the full page width: `@page { size: 80mm auto }` is ignored by emulation and by "save as PDF" at a chosen paper size, so the width is stated rather than inferred.
- [x] Reprint works and is labelled — `REPRINT` plus `issuedAtLocal`. Client-decided; see `DECISIONS.md` for why, and for the limitation that leaves.
- [x] No layout overflow on long product names — `overflow-wrap: anywhere`, with a Vitest case asserting an 80-character name reaches the DOM whole
- [x] A4 fallback readable — a toggle that swaps the `@page` box, since `@page` is document-level and cannot be selected by class

**Also here, and owed from Phase 5:** the completion panel's receipt action. It said "printed receipts arrive in a later milestone" rather than stubbing a button; it now prints, and prints an *original*.

**`window.print()` blocks the tab, and that is accepted narrowly.** Invariant 10 bans `alert`/`confirm`/`prompt` because they stall a queue while a scanner types into whatever has focus afterwards; the difference is that a cashier pressed a button marked Print and is looking at the printer. The rule that follows: nothing auto-prints, pinned by an e2e test that stubs `window.print` and asserts the count is still zero after a sale completes and the preview opens.

## 6.3 Z-report / daily sales

`GET /shifts/{id}/report` and `GET /reports/daily?date=`.

Per shift and per business day:

- Gross sales, discounts, net, tax by rate, total
- Tender breakdown (cash, external card, voucher)
- **Cash reconciliation**: opening float, cash sales, cash refunds, drops/payouts, expected, counted, **variance**
- Voids and refunds, listed individually with actor and reason
- Transaction count, average basket
- Rounding adjustments total (must reconcile — an unexplained variance here means the rounding rule is being applied inconsistently somewhere)

**Business day, not UTC day.** Resolved through `Tenant.TimeZoneId` + `BusinessDayStartOffset`, so a shift closing at 02:00 lands on the previous trading day. Getting this wrong makes every daily figure disagree with what staff remember selling, which destroys trust in the whole reporting section.

`GET /reports/margins` is Owner-only (`CanViewMargins`).

**Exit criteria**
- [x] Z-report totals reconcile with the underlying sales, verified by a test that builds sales through the real endpoints and asserts the report against them. **This is what found the report's version of the receipt bug** — the header is rounded once per sale and the lines are stored at four places, so the tax rows came to €2.8980 against a headline of €2.9000. `Reconciliation.RoundToSum` is now shared by both.
- [x] Cash variance arithmetic correct across sales, refunds, drops and payouts — one test with all four, then a close, asserting the report switches from a live figure to the stored one
- [x] Business-day boundary correct — the 23:30/01:30 test under a 04:00 day start, plus both clock changes; falsified against the naive "convert, subtract, take the date", which is right on 363 days a year
- [x] Voids/refunds listed with actor and reason
- [x] Margins Owner-only, with a negative test (a Manager gets 403)
- [x] A day with no sales renders as zeroes, not an error

**Screens** (not in the original §6.3, added because the phase's goal is a person reading the number): `features/reports/` with a daily report and a per-shift Z-report, plus a nav entry under `CanCloseShift` — the first thing in the app gated on that policy.

**Scope**: `/reports/sales-summary` and `/reports/top-products` are documented in `docs/API.md` and **deferred**, marked there rather than silently skipped. `/reports/daily` covers the single-day case and nothing has a screen for the other two.

**Two things found on the way out, both real and both fixed here:**

1. **A shift was scoped by the day it opened.** An overnight drawer's sales fall in today's window, so the report showed a day of cash takings with no opening float behind them and no shift to say the drawer was uncounted — it read as fully reconciled. Scoped by overlap now.
2. **`GET /stock` ignored `?q=`.** The web client has sent it since 4.3; the handler never declared the parameter. The stock screen's search box has never filtered — it returned an unfiltered first page, which looked right for as long as the shop had fewer products than a page. Fixed by sharing `/products`' predicate, with three tests that go red without it. Not Phase 6's work, but it was blocking Phase 6's suite and shipping a control that does nothing.

## 6.4 Sale history

`src/features/sales/`

- List with filters: date range, register, cashier, shift, type, status
- Detail view: lines, tenders, snapshots, void/refund status, links between a refund and its original **in both directions** (from the original you must be able to see it was refunded)
- Reprint receipt
- Initiate a refund (permission-gated), full or partial
- Search by sale number — the reference a customer reads off their receipt, and therefore the only search that matters at a counter

**Exit criteria**
- [ ] Filters work and compose
- [ ] Detail shows snapshots and refund linkage both ways
- [ ] Refund initiation gated and audited
- [ ] Search by sale number
- [ ] Cursor pagination handles a large history without slowing down

---

## Verification

```powershell
dotnet test
dotnet run --project src/Pos.Api
pnpm --dir src/Pos.Web dev
```

Manual: run several sales across two tax rates with a discount and a cash-rounded total → print a receipt and check the tax breakdown sums → refund one partially → close the shift → open the Z-report and **reconcile it by hand against the sales you just made**. The report is only trustworthy once you have checked it against reality once.

Test the business-day boundary deliberately: create a sale just before and just after the tenant's day start and confirm each lands on the right day.
