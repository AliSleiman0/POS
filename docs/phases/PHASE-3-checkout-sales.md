# Phase 3 — Checkout & Sales (Cash)

**Goal:** a sale can be priced, tendered in cash, and committed exactly once — with numbers that reconcile against the cash in the drawer.

**This is the phase that has to be right.** Everything else is recoverable; wrong money is not. All rules live in `Pos.Core` as pure logic so they can be tested exhaustively without a database.

**Depends on:** Phase 2.

---

## 3.1 Money type

`Pos.Core/Money/Money.cs` — a value object over `decimal`, plus `Pos.Core/Money/Rounding.cs`.

Rules (rationale in [DATA-MODEL.md](../DATA-MODEL.md#money--rounding)):

1. `decimal` only. **Never `float`/`double`** — binary floating point cannot represent 0.10 exactly, and the error compounds across a basket.
2. `MidpointRounding.AwayFromZero`. .NET's default is banker's rounding: 2.5 → 2. A customer will dispute that receipt and be right to.
3. Round **once**, when producing an amount a person pays. Line extensions stay at full precision. Rounding each line and summing yields a total a cent or two off the honest one — the classic penny-off bug, and it makes the Z-report never balance.
4. Arithmetic on raw `decimal` prices outside `Money` is a review failure.

Also `CashRounding` for jurisdictions whose smallest coin exceeds the smallest currency unit (5-cent rounding). The adjustment is a **recorded value on the sale** (`RoundingAdjustment`), never a silent nudge — otherwise the drawer is short by an amount nothing explains.

**Exit criteria**
- [x] `Money` immutable, with operators and explicit rounding methods
- [x] Rounding tests: `.005`, `.015`, `.025`, negatives, and the away-from-zero contract
- [x] A test proving per-line rounding and round-once produce different totals — the bug is real, and the test documents which behaviour is chosen
- [x] Cash rounding produces a recorded, reconcilable adjustment

## 3.2 Pricing engine

`Pos.Core/Pricing/` — `Cart`, `CartLine`, `PricingEngine`, `TaxCalculator`.

Deterministic pipeline, in this order:

```
line subtotal (qty × unit price, full precision)
  → line discount
  → cart discount (apportioned across lines, remainder to the largest line)
  → tax
  → total
  → cash rounding
```

- **Tax on the discounted amount**, never the pre-discount amount.
- **Cart discount is apportioned to lines**, not applied to the total, so per-line tax stays correct when lines carry different tax rates. The apportionment remainder goes to the largest line so the parts sum exactly to the whole.
- **`TaxMode` per tenant.** `Exclusive`: tax added to the line price (US-style). `Inclusive`: tax extracted from a shelf price that already contains it (EU-style), `tax = gross × rate / (1 + rate)`. Decided now because it reinterprets every stored price and is not retrofittable — see [DATA-MODEL.md](../DATA-MODEL.md#entities).
- Pure functions: no DB, no clock, no I/O. Products and rates are passed in.

**Exit criteria**
- [x] Engine is a pure function of (lines, rates, mode, discounts)
- [x] Inclusive and exclusive both correct, each with worked examples as tests
- [x] Mixed tax rates in one cart correct
- [x] Cart discount apportionment sums exactly to the discount given (no lost or gained cent)
- [x] Zero-quantity, zero-price, and 100%-discount lines handled
- [x] `POST /sales/quote` returns exactly what `POST /sales` would compute

## 3.3 Price snapshotting

`SaleLine` stores `Description`, `UnitPrice`, `TaxRate` and `DiscountAmount` **as of the moment of sale**.

Reports must never join to the current `Product.Price`. Otherwise raising a price on Tuesday retroactively rewrites Monday's revenue: the reports stop reconciling with the cash that was actually taken, and nothing surfaces the discrepancy.

**Exit criteria**
- [x] Sale lines carry snapshots
- [x] A test creates a sale, changes the product's price and tax rate, and asserts the historical sale total is unchanged
- [x] A test asserts a report over that period is unchanged too (the snapshot is only useful if the read path honours it)

## 3.4 Cash tender

`Pos.Core/Tenders/` + `Tender` entity.

- A **collection** of tender rows, not a column. Split payments are ordinary retail.
- `Method` discriminator: MVP implements `Cash` and `External` (standalone card terminal, staff key in the amount). `Card` is a new row type in Phase 11 — additive, no `Sale` migration.
- Change due = `sum(tenders) − total`. Over-tender is normal; under-tender is rejected as an incomplete sale.
- `Money` handles the arithmetic.

**Exit criteria**
- [x] Multiple tenders on one sale
- [x] Change calculated correctly, including exact tender (zero change)
- [x] Under-tender rejected with a clear `problem+json` type
- [x] Adding a hypothetical new method requires no `Sale` schema change (confirm by reasoning through it, and record the conclusion)

## 3.5 Idempotent submit

`Pos.Core/Idempotency/` + `IdempotencyRecord` + middleware or endpoint filter in `Pos.Api`.

Contract (full detail in [DATA-MODEL.md](../DATA-MODEL.md#idempotency)):

1. Client generates a GUID **before its first attempt**, reuses it on every retry.
2. Server inserts the key in the **same transaction** as the work. The unique index is the guarantee — not a check-then-insert, which races.
3. Key present, request hash matches → return the **stored original response**.
4. Key present, request hash differs → `409`. The same key was reused for different content; that is a client bug worth surfacing.

**Why now rather than later:** a cashier double-tapping on a slow connection must not charge twice — that is a today problem, not an offline problem. And Phase 9's outbox is this contract plus a retry loop, which is why offline needs no separate sync API. Retrofitting it means revisiting every write path.

**Exit criteria**
- [x] Unique index on `(tenant_id, client_transaction_id)`
- [x] Replay returns the original sale; exactly one row exists — tested with **concurrent** duplicate requests, not sequential ones
- [x] Same key, different body → `409`
- [x] Applied to sales, voids, refunds, stock adjustments, shift open/close

## 3.6 Atomic commit

`Pos.Api/Endpoints/SaleEndpoints.cs` + `Pos.Data/Sales/SaleWriter.cs`.

One transaction containing: idempotency key insert → sale number assignment → `Sale` → `SaleLine`s → `Tender`s → `StockMovement`s → `StockItem.OnHand` updates.

- **Sale number** from a per-tenant counter row locked inside the transaction, not `MAX()+1` read outside it (which produces duplicates under concurrency). Gaps look like deleted records to an auditor, so the counter is not incremented speculatively.
- **Optimistic concurrency** on `StockItem` via `xmin`. Two registers selling the last unit collide; the loser retries against fresh state.
- **Insufficient stock does not block the sale.** The customer is standing there holding the item. The sale completes, stock may go negative, and a `StockDiscrepancy` is flagged for staff review — per `DECISIONS.md`, flag rather than silently corrupt counts. Refusing the sale is the wrong behaviour and is how POS systems get thrown out.

**Exit criteria**
- [x] All writes in one transaction; a forced mid-transaction failure leaves nothing behind
- [x] Concurrent sales produce unique, gapless-in-practice sale numbers (test with parallel requests)
- [x] Concurrent last-unit sale: both succeed, stock goes negative, discrepancy flagged
- [x] `GET /stock/discrepancies` lists it

## 3.7 Append-only ledger

- `POST /sales/{id}/void` — sets `Status = Voided` + `VoidedBy`/`VoidReason`, writes compensating `StockMovement`s. **No delete.**
- `POST /sales/{id}/refund` — creates a **new** `Sale` with `Type = Refund`, negative amounts, `OriginalSaleId` set. Partial refunds supported (a subset of lines/quantities).
- Both gated (`CanVoidSale`, `CanRefund`) and audited.
- A completed sale is never updated. Corrections are new linked rows.

**Exit criteria**
- [x] No code path updates or deletes a `Completed` sale — verified by grep and by a test attempting it
- [x] Void writes compensating movements; stock returns to its prior level
- [x] Partial refund correct; over-refunding beyond the original quantity rejected
- [x] Double-void and double-refund rejected (and idempotency-safe on retry)

## 3.8 Register shifts

`Shift`, `CashMovement`, endpoints per [API.md](../API.md#shifts--shifts).

- Open with `OpeningFloat`; `409` if that register already has an open shift
- Cash movements: drop, payout, petty cash, correction — `reason` required
- Close with `CountedCash` → `ExpectedCash = float + cash sales − cash refunds − drops/payouts`, `Variance = counted − expected`
- **A sale requires an open shift.** Without one there is nothing to reconcile the drawer against, and "we're £12 short" is unanswerable — which is the single report an owner checks daily.

**Exit criteria**
- [x] Open/close lifecycle; second concurrent open on one register rejected
- [x] Expected cash arithmetic correct across sales, refunds, drops and payouts
- [x] Variance computed and stored
- [x] A sale with no open shift is rejected
- [x] Closing a shift with open sales in flight behaves deterministically

## 3.9 Tests

The heaviest test phase, deliberately.

- **Unit (`Pos.Core.Tests`)** — pricing across both tax modes, mixed rates, discount apportionment, rounding boundaries, tender/change, shift arithmetic. Property-based tests where they fit: apportioned discounts always sum to the total discount; `OnHand` always equals the movement sum.
- **Integration (`Pos.Api.Tests`)** — concurrent idempotent replay, concurrent last-unit sale, sale-number uniqueness under parallel load, full refund flow, shift close arithmetic, tenant isolation for every new endpoint.

**Exit criteria**
- [x] Pricing and rounding covered exhaustively, not just happily
- [x] Concurrency tests actually run in parallel (a sequential "concurrency" test proves nothing)
- [x] Isolation tests extended to sales, tenders, shifts, discrepancies

---

## Verification

```powershell
dotnet test
dotnet run --project src/Pos.Api
```

Via Swagger, end to end: open a shift → `POST /sales/quote` and check the totals by hand → `POST /sales` with a cash tender → confirm change due → replay the same `Idempotency-Key` and confirm one sale exists → check stock decremented → refund a line → close the shift and check the variance.

**Also verify the arithmetic by hand at least once**, on paper, for an inclusive-tax cart with a mixed-rate discount. It is the one calculation in the system that a test can confirm is *consistent* but not that it is *correct*.
