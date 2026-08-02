# Session Handoff

**Written:** 2026-08-02 · **Branch:** `phase-3/checkout-sales` · **Phase 3 complete — start Phase 4.1**

> A sale can be priced, tendered in cash, and committed exactly once, with numbers that reconcile against the drawer. **846 tests green locally** — 261 Core, 106 Data, 479 Api. Release build warning-free with `$env:CI="true"`. The whole path was driven by hand against the dev database, and the shift's expected cash matched an independent hand computation. **CI has not run: the branch is unpushed and there is no PR.** Do that first, and do not call Phase 3 done until the runner agrees.

> This file is session state, not durable truth. Overwrite it when you finish. Durable decisions belong in [`DECISIONS.md`](../DECISIONS.md), durable progress in [`ROADMAP.md`](ROADMAP.md).

## Read first

1. [`DECISIONS.md`](../DECISIONS.md) → the four **"Resolved 2026-08-01/02 (during Phase 3.x)"** sections. Roughly thirty decisions; the ones Phase 4 inherits are listed under [What Phase 4 inherits](#what-phase-4-inherits).
2. [`docs/phases/PHASE-4-web-shell-catalog.md`](phases/PHASE-4-web-shell-catalog.md) → **§4.1**, the next milestone.
3. [`docs/API.md`](API.md) → the sales and shifts contracts, now written up in full.
4. [`CLAUDE.md`](../CLAUDE.md) — the 10 invariants.

## State

Eight commits on `phase-3/checkout-sales`, none pushed:

```
ab99f4e  3.1  a Money value type, rounded once and away from zero
ba90685  3.2  the pricing engine, inclusive and exclusive tax modes
5e87163  3.3  sale, shift and tender entities with snapshotted line prices
c8caf56  3.4  cash tender, change due, and under-tender refused
ba82661  3.5  idempotent submit, one mechanism for sales, refunds, shifts and adjustments
bfaf7fc  3.6  one transaction for the sale, its stock and its number
2a262e3  3.7  voids and refunds as new linked rows, never an edit
525ea83  3.8  register shifts, expected cash and variance
```

Three migrations: `MoneyType`, `Sales`, `Idempotency`. The dev database is migrated and seeded.

---

## What Phase 4 inherits

The web app is a client of all of this, so these are the ones that shape it:

1. **API DTOs are `decimal`, not `Money`.** The JSON contract is plain numbers, and
   `SaleContractTests.No_endpoint_dto_declares_a_money_property` fails the build if that ever
   slips. So the generated TypeScript client sees `number` everywhere — and per CLAUDE.md,
   **money is never a JS `number` in a calculation**: the server computes every total, the
   client displays it.
2. **`POST /sales/quote` exists so the register never prices anything itself.** It takes the
   same cart as `POST /sales`, needs no register, shift, tenders or idempotency key, and is
   guaranteed to agree with the sale because both build their `Cart` through one function.
3. **Every 🔒 endpoint needs `Idempotency-Key`**, generated *before the first attempt* and
   reused on retry. Missing or malformed is a `400` with the header named in `errors`. This is
   the mechanism Phase 5.5's double-submit safety uses — **a disabled button is not it**.
4. **A sale needs an open shift**, so the register's first screen after login is "open a
   drawer". `GET /shifts/current?registerId=` answers `404` when there is none.
5. **A replay returns the original status and body byte-for-byte, with `Idempotent-Replay:
   true`** — but *not* other headers. A replayed `201` carries no `Location`.

## Things that will bite you

New in Phase 3 (1–12); the rest carried forward and still true.

1. **A namespace may not share a name with a type inside it.** `Pos.Core.Money` makes `Money` a
   member of `Pos.Core`, and enclosing-namespace members outrank `using`-imported types — so
   every unqualified `Money` under `Pos.Core.*` is **CS0118**. The namespace is `Pos.Core.Monetary`.
2. **EF cannot aggregate a value-converted property.** `SumAsync` over a `Money` column does not
   translate. Shift arithmetic reads its sums with `db.Database.SqlQuery<decimal>`, writing the
   `tenant_id` predicate **by hand** because the query filter does not compose over raw SQL.
3. **`Properties<decimal>()` does not cover `Money`.** It matches the CLR property type. A
   `Money` property with no `HavePrecision` maps at `numeric(18,2)` and silently truncates.
4. **Minimal-API endpoint filters run *after* model binding**, so the body is already consumed.
   `Program.cs` calls `EnableBuffering()` before routing; the idempotency filter throws rather
   than hashing zero bytes if that is ever removed.
5. **EF's `SqlQuery` composes its argument into a subquery**, and Postgres will not accept a
   data-modifying statement there. The sale-number upsert goes through raw ADO, enlisted in the
   ambient transaction explicitly.
6. **`ON CONFLICT (tenant_id)` needs a unique constraint on `(tenant_id)` alone.** That is why
   `ux_sale_sequence_tenant` is not the usual `(tenant_id, id)` composite.
7. **A `SqlQuery<T>` result column must be aliased `"Value"`.** Every raw query here does.
8. **`CatalogFixture` seeds stock rows with no matching movements**, so `OnHand == SUM(movements)`
   is *false* in the Api test fixture. Assert `openingBalance + ledger` there. `CatalogGraph`
   (Data tests) does write the opening receipt, so the invariant holds outright.
9. **The endpoint's shift check masks the writer's.** Deleting the writer's `FOR SHARE` check
   left the whole Api suite green; `SaleWriterTests` in `Pos.Data.Tests` exists to test the
   writer's guards with no endpoint in front of them.
10. **Two units in one refund cost a cent less than two separate one-unit refunds** (2.95 vs
    2.96). That is round-once working, not a bug.
11. **`Subtotal` is derived, not summed.** If someone "simplifies" it back to a fourth
    independent sum, only the property test over random carts notices.
12. **A migration class named after a type collides with it.** `migrations add Money` produced
    `Pos.Data.Migrations.Money`, ambiguous with `Pos.Core.Monetary.Money`. It is `MoneyType`.
13. **A model change touches four files** — configuration, migration, `.Designer.cs` and
    `AppDbContextModelSnapshot.cs`. A partial edit fails the *whole* Data and Api suite at
    fixture init with `PendingModelChangesWarning`, on an error unrelated to what you did.
14. **An applied migration does not re-run**, so a migration adding a tenant table must call
    `ApplyTenantRowLevelSecurity()` — and in `Down` call **`Apply`, not `Remove`**.
15. **A retrying execution strategy refuses a user-initiated transaction.** Wrap the unit in
    `db.Database.CreateExecutionStrategy().ExecuteAsync(...)`.
16. **There are no navigation properties, so there is no FK fixup.** Save principals before
    dependents. `SaleWriter` saves the sale, then its lines, then the movements.
17. **A stale build can make a correct migration emit wrong SQL.** `Remove-Item -Recurse
    src/Pos.Data/obj, src/Pos.Data/bin` before debugging generated SQL.
18. **xUnit here is 2.9.3**, so there is no `TestContext.Current`.
19. **`InvariantGlobalization` is on**, so `CultureInfo.GetCultureInfo("fr-FR")` *throws*. Build a
    `NumberFormatInfo` by hand if a test needs a non-invariant separator.
20. **A shared `WebApplicationFactory` has no reset between tests.** `TwoTenantWorld` is
    read-only; `CatalogSandboxAsync()` asserts *contains*, never counts; anything asserting an
    exact count uses `factory.TradingTenantAsync()`, which makes its own tenant.
21. **`problem+json` carries a per-request `traceId`.** Compare `type`/`title`/`status`.
22. **`GetProperty("errors")` throws `KeyNotFoundException`** when the response is not a
    validation problem — which reads as an unrelated failure. Assert the status first.
23. **`[CallerFilePath]` is a lie under CI.** `$env:CI="true"` reproduces CI-only behaviour.
24. **`dotnet run` uses `launchSettings.json` and ignores `ASPNETCORE_URLS`.** Port 5013.
25. **`dotnet ef database update` needs the owner connection string passed explicitly.**
26. **Kill stray `Pos.Api` processes before rebuilding** — `Stop-Process`, not `pkill`.
27. **A superuser bypasses RLS unconditionally.** Isolation assertions run on `pos_app`.
28. **`GET /registers` is a bare array, not a cursor page.** So are the other two un-paginated
    endpoints.

## What the falsification pass found

**Fifteen deliberate breaks. Thirteen went red immediately; two caught nothing and both gaps
are now closed.**

The two that mattered:

- **Per-line rounding in the pricing engine was caught by nothing.** Accumulating
  `lineTotal.Round()` instead of `lineTotal` passed every worked example *and* the invariant-1
  property, because `Subtotal` is derived from the same wrong number. Closed by three tests,
  one per rounded column — `TaxTotal` got its own because it is the figure that reaches a tax
  authority.
- **Deleting `SaleWriter`'s shift check left the entire Api suite green**, because the
  endpoint's own check masks it under sequential conditions. Closed by `SaleWriterTests` in
  `Pos.Data.Tests`, which exercises the writer with no endpoint in front of it.

The thirteen that went red as expected: RLS omitted from a migration; `DateTime.UtcNow` in Core;
`TimeProvider.System` inside a lambda; tax on the gross; `Subtotal` independently summed;
`MAX()+1` sale numbering; the discrepancy boundary at `<= 0`; a refund priced from the catalog;
voided refunds counted toward the remaining quantity; voiding a refunded sale; voided sales
counted in expected cash; the tendered note counted instead of tendered-less-change; and the
status guard dropped from the closing `UPDATE`.

## Verified by hand

- **The arithmetic, on paper.** An inclusive-tax cart with two rates and a cart discount that
  does not divide evenly was computed independently and then asserted:
  `HandCheckedCartTests`. Total 12.97, tax 2.02, discount 4.22, derived subtotal 15.17, shares
  4.1653 + 0.8347 = 5.0000 exactly. An exclusive control gives 15.46, so the mode is not being
  ignored.
- **The whole path, against the dev database and a real API**: open shift → quote → sell →
  replay the key (one sale, not two) → stock 48 → 46 → oversell to −3 and see the discrepancy
  listed → refund a line → close the shift. Expected cash `100 + 2.40 + 11.85 − 1.20 = 113.05`
  matched the endpoint exactly.
- **Invariant 1 on real rows**: all three sales in the dev database satisfy
  `Total == Subtotal − DiscountTotal + TaxTotal + RoundingAdjustment`.

## Outstanding / deferred

- **CI has not run on this work.** Push the branch and open the PR; that is the first thing.
- **Existing dev databases carry stock drift.** `tools/Pos.Seed` used to write stock rows with
  an opening balance and no matching movement, so `OnHand != SUM(movements)` on any database
  seeded before this commit. Fixed for *fresh* seeds; an existing one needs the opening
  receipts adding by hand. **Do not "fix" it with `RebuildOnHand`** — that would set on-hand to
  the movement sum and discard the seeded opening stock.
- **No audit log.** Phase 7.2. Price overrides are authorized and recorded on the sale line
  (`IsPriceOverridden`, `OverriddenBy`), but API.md's older claim that "the attempt is audited"
  is not yet true — the line is the whole trail.
- **`GET /settings` and `PUT /settings` are not built**, so `TaxMode` and
  `CashRoundingIncrement` are settable only by SQL or the seeder, and "taxMode is rejected once
  sales exist" is enforced nowhere. `Sale.TaxMode` is snapshotted for exactly this reason.
- **`GET /sales/{id}/receipt` and `GET /shifts/{id}/report` are documented and unbuilt** —
  Phase 6.1 and 6.3.
- **`?from=`/`?to=` on `GET /sales` are not implemented.** Date-range reporting is Phase 6.
- **`CursorPaging` is still ascending-only**, so `GET /sales` pages oldest-first. Phase 6 has
  the screen to argue from.
- **Percentage discounts do not exist.** Absolute amounts only; a percentage is a client
  computation.
- **`Tender.Method` accepts `Cash` only.** `External`, `Card` and `Voucher` are declared so the
  discriminator claim holds, and refused at the endpoint.
- **`RebuildOnHand` still has no route.**
- **`TaxClass` has no deactivate and no `IsActive`.**
- **`limit=abc` returns a bare 400 with no `problem+json` body.** A global `UseStatusCodePages`
  would fix every bare body at once and belongs in its own commit.
- **`dotnet dev-certs https --trust`** still not run.
- **Playwright browsers not installed** — Phase 4.
- **Production `pos_app` password** — Phase 8.2.
- **A validly signed token is trusted for whatever tenant it names** — accepted and pinned by
  `ForgedTenancyTests`.
- **CI actions emit a Node 20 deprecation warning.**
- **`Microsoft.OpenApi` pinned to 2.11.0** for GHSA-v5pm-xwqc-g5wc.

## Still genuinely open

**Pricing/business model** — one-time purchase vs. recurring, given that we host. Blocks nothing
until Phase 10, but must be settled before quoting a price.
