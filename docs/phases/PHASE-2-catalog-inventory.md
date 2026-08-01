# Phase 2 — Catalog & Inventory

**Goal:** products, barcodes and categories are manageable through the API, and stock is a movement ledger rather than a mutable number.

**Depends on:** Phase 1 fully green (1.7 especially).

---

## 2.1 Entities

In `Pos.Core/Entities/`, all deriving `TenantEntity` — full field lists in [DATA-MODEL.md](../DATA-MODEL.md#catalog).

`Category`, `TaxClass`, `Product`, `Barcode`, `StockItem`.

EF configurations in `Pos.Data/Configurations/` — one `IEntityTypeConfiguration<T>` per entity, applied by assembly scan. Not fluent config buried in `OnModelCreating`, which becomes unreadable at twenty entities.

Design points worth restating because they are the ones usually got wrong:

- **`TaxClass` is a separate entity, not a rate on the product.** Tax rates change by legislation. A rate on each product means a VAT change is a mass update; a class means it is one row. Historical sales are unaffected either way because rates are snapshotted at sale time.
- **`Barcode` is a collection.** Multipacks, re-labelled stock and supplier variations all mean one product legitimately scans as several codes. One-barcode-per-product is the most common retail catalog modelling mistake and it is discovered only after data exists.
- **`Product.Unit`** distinguishes `Each` from `Kilogram`/`Litre`. Quantity is `numeric(19,4)`, because 0.350 kg of cheese is an ordinary sale.
- **`StockItem.RowVersion`** maps to Postgres `xmin` for optimistic concurrency — the mechanism Phase 3.6 relies on to detect two registers selling the last unit.

**Exit criteria**
- [x] Entities + configurations, migration generated and applied — `CatalogAndInventory`, RLS re-applied in both `Up` and `Down`
- [x] All money columns `numeric(19,4)`, quantities `numeric(19,4)` — `DatabaseSchemaTests` scans every numeric column in the schema, with `tax_class.rate` at `(6,4)` as the one stated exception
- [x] Indexes from [DATA-MODEL.md](../DATA-MODEL.md#key-indexes) present, each leading with `tenant_id` — asserted by name, column order and uniqueness in the catalog, and model-wide in `TenantModelTests`
- [x] Global query filter demonstrably applies to each new entity (no per-entity registration needed) — model-wide by reflection, plus `CatalogTenantIsolationTests` per entity

**Also landed here, decided during the milestone** (see [`DECISIONS.md`](../../DECISIONS.md#resolved-2026-08-01-during-phase-2)):

- **Foreign keys carry `tenant_id`**, pointing at `ak_*_tenant_id_id` alternate keys, because RI checks bypass RLS. `RESTRICT`, never `CASCADE`.
- **`StockItem.RowVersion` maps to `xmin`** — `UseXminAsConcurrencyToken()` no longer exists in Npgsql 10; the convention now matches on `uint` + generated-on-add-or-update + concurrency token.
- **`Product.NormalizeSku`** (trim + invariant uppercase), because the unique index lands here and decides whether `abc` and `ABC` are one product or two.

## 2.2 CRUD API

`Pos.Api/Endpoints/ProductEndpoints.cs`, `CategoryEndpoints.cs`, `TaxClassEndpoints.cs`. Routes and policies per [API.md](../API.md#products--products).

- Cursor pagination via a shared helper — written once, used everywhere
- Search by name and SKU, case-insensitive
- **No `DELETE` verb for products.** `POST /products/{id}/deactivate` sets `IsActive = false`. Sale lines reference products permanently; a delete either orphans history or cascades a customer's sales away.
- `costPrice` and margin fields are **omitted from the response** for callers without `CanViewMargins` — not sent and hidden client-side. Anything sent to the browser is readable.
- Request validation: prices non-negative, SKU non-empty and unique per tenant, quantity precision respected

**Exit criteria**
- [x] Full CRUD (minus delete) for products, categories, tax classes — plus `activate`, added because `deactivate` alone made a mis-click permanent (PUT deliberately does not carry `isActive`)
- [x] Cursor pagination works and is stable across concurrent inserts — `ProductListTests.A_product_inserted_behind_the_cursor_does_not_disturb_the_next_page`, and the row-value keyset asserted structurally in `CursorPaginationTests`
- [x] `costPrice` absent from a Cashier's response payload — `ProductMarginTests`, whose first test asserts an *Owner* sees 0.5500 so the absence assertions are not vacuous
- [x] Validation failures return `problem+json` with per-field errors — `ProductValidationTests`, including three bad fields producing three keys
- [x] Duplicate SKU within a tenant rejected; the same SKU across two tenants accepted — `ProductCrudTests`, including the case- and whitespace-only difference

**Also landed here, decided at the start of the milestone:**

- **Scalar** at `/scalar/` in Development, which is what makes 2.5's click-through possible at all.
- **`pg_trgm` + `btree_gin`** for the `?q=` contains search. `btree_gin` is not optional: it supplies GIN an operator class for `uuid`, without which `tenant_id` cannot lead the index and `DatabaseSchemaTests` fails.
- **SKU is matched exactly**, not by trigram — short codes, and trigrams need three non-wildcard characters.
- **Barcodes stay in 2.3**, so `POST /products` takes none and the primary-barcode rule is still 2.3's to decide.
- **Category cycles** are checked in the write path against the whole hierarchy read in one query.

## 2.3 Barcode lookup

`GET /products/by-barcode/{code}` — the hottest read in the product. Every scan hits it.

- Unique index on `(tenant_id, code)`
- Returns the product with its price and tax class in one round trip; the register must not need a second call to price an item
- `404` for unknown codes, which the register turns into "unknown item — add it?" rather than an error dialog
- `POST /products/{id}/barcodes` and `DELETE /products/{id}/barcodes/{barcodeId}`. Barcodes may be deleted (a mis-scanned label is data entry, not history); products may not.

**Exit criteria**
- [x] Lookup returns product + price + tax rate in one query — `BarcodeLookupTests.A_scan_reaches_all_three_tables_in_one_statement` asserts on the SQL `ProductEndpoints.LookupQuery` actually generates (it is `internal` for this reason): one `FROM barcode`, two `INNER JOIN`s, and the tenant predicate on all three tables
- [x] Duplicate barcode within a tenant rejected; same code across tenants accepted — `BarcodeTests`, including the collision landing on a *different* product, which is the case that matters
- [x] Unknown code → `404`, and so is a blank one — `BarcodeLookupTests`
- [x] Multiple barcodes per product work end to end — the fixture gives Still Water two codes, and both scan to the same product

**Also landed here, decided during the milestone:**

- **`isPrimary` stays advisory** — no filtered unique index. See [`DECISIONS.md`](../../DECISIONS.md#resolved-2026-08-01-during-phase-23-and-24).
- **`GET /products/{id}/barcodes`** was added, and is not in the original contract: without it a client cannot learn a barcode id, so `DELETE .../{barcodeId}` is unaddressable. A bare array, not a cursor envelope — it is a bounded sub-resource, not a tenant-wide list.
- **A deactivated product still scans**, carrying `isActive: false`, so the register can say "not for sale" rather than inviting a duplicate.
- **No migration.** 2.1 had already built `barcode`, `ux_barcode_tenant_code` and `ix_barcode_tenant_product`.
- **`IsolationCase.UrlFor` now fills every route parameter and throws on an arity mismatch** — the `DELETE` route has two, and an unsubstituted one answers the same 404 the theory asserts.

## 2.4 Stock movement ledger

`StockMovement` (append-only) + `StockService` in Core with the persistence port implemented in `Data`.

- `POST /stock/adjustments` — `reason` is **required**, not optional. A stock adjustment without a reason is exactly the record you need six months later and won't have.
- Every adjustment writes a movement **and** updates `StockItem.OnHand` in one transaction
- `GET /stock/{productId}/movements` — the paginated ledger
- A `RebuildOnHand` maintenance routine that recomputes `OnHand` from the ledger. It is the proof that the ledger is the truth and the cache is derived; it is also what you run when the invariant drifts.

**Why a ledger:** the question is never "what is on hand", it is "why is on hand wrong?" A bare number cannot answer that, and shrinkage is a real problem staff need to investigate. It is also what makes Phase 9's offline reconciliation tractable — replaying queued movements against a ledger works; reconciling two disagreeing counters does not.

**Exit criteria**
- [x] Adjustments write a movement and update `OnHand` atomically — one transaction inside an execution strategy in `StockLedger`; `StockLedgerTests.A_movement_that_cannot_be_written_leaves_neither_the_row_nor_the_total` forces a foreign-key failure part-way through and asserts neither survives
- [x] `reason` required, enforced server-side — `StockAdjustmentTests`, including the blank and whitespace cases
- [x] Ledger endpoint paginated — oldest-first, and `StockMovementListTests.Walking_the_ledger_a_page_at_a_time_sees_every_row_exactly_once` is the real test: this is the only list keyed on a timestamp
- [x] `RebuildOnHand` reproduces `OnHand` exactly, with a test over a mixed movement history — and one that corrects drift written behind the ledger's back, without which the rebuild is only reproducing a number that was already right
- [x] Invariant test: `OnHand == sum(movements)` after a randomised sequence — seeded, so a failure is reproducible

**Also landed here, decided during the milestone:**

- **`IStockLedger` is the first persistence port Core declares**, because the operation is a transaction with a concurrency token rather than a save, and Phase 3's sale path must join it.
- **Not idempotent.** `POST /stock/adjustments` was marked 🔒 in `API.md`; that is deferred to 3.5 and the mark removed until then, with a test pinning the current behaviour.
- **`GET /stock/discrepancies` deferred to 3.6** — nothing flags an oversell until the sale path exists.
- **`GET /stock` is driven off products, not stock rows**, so an uncounted product reads zero instead of vanishing, and the list can be ordered by something a human recognises.
- **`RebuildOnHand` has no route.** It is a maintenance routine on the port.

## 2.5 Tests

- **`Pos.Core.Tests`** — catalog rules, stock arithmetic, validation
- **`Pos.Data.Tests`** — configurations, indexes present, filters apply to the new entities
- **`Pos.Api.Tests`** — CRUD happy paths, validation failures, `404` vs `403`, policy enforcement per endpoint, **and tenant isolation for every new endpoint** (extend the 1.7 suite rather than starting a parallel one)

**Exit criteria**
- [x] Every new endpoint has an isolation test — seven rows added to `IsolationManifest`; two are `Exempt` and each names the test where the coverage actually lives. `EndpointCoverageTests` fails the build on a mapped endpoint with no row, in both directions.
- [x] Every policy-gated endpoint has a negative test proving an under-privileged caller is rejected — `NegativeAuthorizationTests` from the manifest's `Refused` lists, plus the exact 401-vs-403 pinned by hand for the barcode routes, the ledger and the adjustment endpoint.

**The falsification pass.** Six deliberate breaks, six caught, and two of them told us something:

- Stopping `OnHand` following the ledger failed four tests — but **not the two rebuild tests**, correctly: a rebuild repairs drift, so it is insensitive to the write path being broken. Those tests protect the repair, not the write.
- Removing the product-exists check from the movements endpoint failed its own test **and the by-id isolation theory** — without the check, a cross-tenant request answers 200 with an empty ledger rather than 404. That row is not vacuous.
- Dropping the `TrackStock` filter from `GET /stock` failed its own test and the collection isolation theory, because the expected ids are the stocked products rather than all of them.
- Serving the scan from the cost-bearing projection failed three tests, including the one that reads the generated SQL.
- Matching a barcode delete on the barcode id alone, and letting a receipt carry a negative quantity, each failed at both the layer that implements the rule and the layer that exposes it.

---

## Verification

```powershell
dotnet ef database update --project src/Pos.Data --startup-project src/Pos.Api `
  --connection "Host=localhost;Port=5432;Database=pos_dev;Username=pos;Password=dev_only_not_a_secret"
dotnet run --project tools/Pos.Seed
dotnet test
dotnet run --project src/Pos.Api
```

Via **Scalar** at `http://localhost:5013/scalar/` (added in 2.2 — the phase originally said Swagger, and no such UI ever existed): create a tax class → category → product → two barcodes → look up by each barcode → receive stock → adjust with a reason → read the ledger → confirm `OnHand` matches the sum.

Scalar is mapped only in `Development`, and the test host runs in `Testing`, which is why it needs no isolation-manifest row.
