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
- [ ] Print preview correct at 80mm
- [ ] Reprint works and is labelled
- [ ] No layout overflow on long product names (wrap, don't clip — a clipped name on a receipt is a dispute)
- [ ] A4 fallback readable

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
- [ ] Z-report totals reconcile with the underlying sales, verified by a test that builds sales and asserts the report
- [ ] Cash variance arithmetic correct across sales, refunds, drops and payouts
- [ ] Business-day boundary correct — test a 23:30 and a 01:30 sale under a tenant with a 04:00 day start
- [ ] Voids/refunds listed with actor and reason
- [ ] Margins Owner-only, with a negative test
- [ ] A day with no sales renders as zeroes, not an error

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
