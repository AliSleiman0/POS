# POS — Data Model

Postgres, shared database, every tenant-owned row carries `TenantId`. Enforcement mechanics are in [ARCHITECTURE.md](ARCHITECTURE.md#multi-tenancy). This file defines the entities and the rules that make the numbers trustworthy.

## Conventions

| Concern | Rule |
|---|---|
| Primary keys | `Guid` (UUIDv7 where available — time-ordered, so index locality is preserved without leaking a sequential count of your business) |
| Money | C# `decimal` ↔ Postgres `numeric(19,4)`. **Never `float`, `double`, or `money`.** |
| Quantity | `numeric(19,4)` — weighed goods exist (0.350 kg of cheese) |
| Timestamps | `timestamptz`, always UTC |
| Text | `text` with a length check constraint, not `varchar(n)` |
| Enums | Stored as `int` or `text` in C#-owned enums, not Postgres `ENUM` types (migrating a Postgres enum is painful) |
| Naming | `snake_case` in the DB, `PascalCase` in C#, mapped by a naming convention — set once in `AppDbContext`, never per-property |
| Deletes | Soft-delete (`IsActive`) for catalog; **no delete at all** for financial records |

Every tenant-owned entity derives from:

```csharp
public abstract class TenantEntity
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }          // stamped by interceptor, never by callers
    public DateTimeOffset CreatedAt { get; set; }
    public Guid? CreatedBy { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
}
```

**Every index on a tenant table leads with `TenantId`.** A query always filters by tenant (the global filter guarantees it), so a leading `TenantId` is what makes the index usable rather than scanned.

---

## Money & rounding

The rules that decide whether a customer's receipt adds up.

1. **`decimal`, 4 decimal places stored, 2 displayed.** Four gives room for unit prices like €0.1650 and for tax-inclusive back-calculation without accumulating error.
2. **`MidpointRounding.AwayFromZero`** — the "round half up" everyone expects. .NET's default is banker's rounding, which rounds 2.5 → 2 and will produce a receipt a customer disputes.
3. **Round once, at the boundary.** Line extensions are computed at full precision; rounding happens when producing the amount a person pays. Rounding every line and summing produces a total that differs from the honest one by cents — the classic penny-off bug.
4. **Tax is computed on the discounted amount**, never the pre-discount amount.
5. **Cash rounding is separate and explicit.** Where the smallest coin is larger than the smallest currency unit (5-cent rounding), the adjustment is a **recorded line** on the sale, not a silent nudge of the total. It must reconcile in the Z-report.

A `Money` value object in `Pos.Core` owns this. Raw `decimal` arithmetic on prices outside it is a code-review failure.

---

## Entities

### Tenancy & identity

**`Tenant`** — a customer business. Not a `TenantEntity` (it *is* the tenant).
`Id`, `Name`, `Slug` (unique), `IsActive`, `CurrencyCode`, `TimeZoneId`, `TaxMode` (`Inclusive`|`Exclusive`), `BusinessDayStartOffset`, `AddressLine?`, `TaxNumber?`, `ReceiptHeader?`, `ReceiptFooter?`, `CreatedAt`.

- `TimeZoneId` + `BusinessDayStartOffset` are what let a shift closing at 02:00 land on the correct trading day.
- The four receipt fields (Phase 6.1) are all nullable, so a shop that has filled none of them in still prints a receipt — the lines it has no content for are simply absent. `AddressLine` is deliberately **one unstructured multi-line field**: a receipt prints it verbatim, nothing parses it, and street/city/postcode columns would have to be right for every country the product is sold in. `TaxNumber` is separate rather than folded into `ReceiptHeader` because it is a distinct fact a tax authority looks for and the renderer has to label it.
- **`TaxMode` is set at onboarding and effectively immutable.** Flipping it reinterprets every stored price. EU retail quotes tax-inclusive shelf prices; US retail adds tax at the till. Getting this wrong is not a display bug, it is a wrong-price bug.

**`ApplicationUser`** — `IdentityUser<Guid>` + `TenantId`, `DisplayName`, `PinHash`, `IsActive`, `LastLoginAt`.

- Identity's default unique index on `NormalizedEmail` is **replaced** with composite `(TenantId, NormalizedEmail)`. Otherwise one person cannot be a user at two tenants, and onboarding fails with an opaque duplicate-key error.
- `PinHash` is unique per `(TenantId, PinHash)`? **No** — do not enforce that. It would let an attacker enumerate which PINs are taken. Collisions are resolved by the user picking the employee they are, or by PIN + employee selection.

**`Register`** — a physical till. `Name`, `DeviceTokenHash`, `IsActive`, `LastSeenAt`.

**`RefreshToken`** — `UserId`, `TokenHash`, `ExpiresAt`, `RevokedAt`, `ReplacedByTokenId`, `FamilyId`. Rotated on use; reuse of a rotated token revokes the whole `FamilyId`.

### Catalog

**`Category`** — `Name`, `ParentCategoryId?`, `SortOrder`, `IsActive`, `StationId?` (Phase 10 — where a restaurant configures kitchen routing, inherited down the hierarchy).

**`TaxClass`** — `Name`, `Rate` (`numeric(6,4)` — 0.2000 = 20%), `IsDefault`.
A class, not a bare rate on the product: rates change by law, and a rate change must not require touching every product. Historical sales keep their snapshotted rate regardless.

**`Product`** — `Sku` (unique per tenant), `Name`, `Description`, `CategoryId?`, `TaxClassId`, `UnitPrice`, `CostPrice?`, `Unit` (`Each`|`Kilogram`|`Litre`), `IsActive`, `TrackStock`.

- **Never hard-deleted.** Sale lines reference products forever; a delete either orphans history or cascades away a customer's sales records.
- `CostPrice` drives margin reporting and is gated behind `CanViewMargins`.
- `TrackStock = false` for services and open-price items.
- **`IsModifier`** and **`StationId?`** arrive in Phase 10 and are `false`/`null` on every retail product — see *Restaurant* below. A modifier is an ordinary product that the flag keeps out of the register grid, the product list and the offline mirror; the station is the override on kitchen routing, which is normally configured on the category.

**`Barcode`** — `ProductId`, `Code`, `IsPrimary`.

- **Many per product** — multipacks, re-labelled stock, supplier variations, and the same item scanning differently across two suppliers. One-barcode-per-product is the single most common retail catalog modelling mistake.
- Unique index on `(TenantId, Code)`, and this is the hottest read in the system.
- **`IsPrimary` is advisory and unconstrained** (Phase 2.3). Nothing stops two primaries on one product: it is a label/display hint, and a filtered unique index would make EF treat the foreign-key index as covered while turning "make this the label code" into a clear-then-set across two saves. Codes are trimmed on the way in but **not** case-folded, unlike a SKU — GS1-128 payloads carry case-significant data.
- Barcodes **may** be hard-deleted, unlike products. Nothing financial points at one; a sale line points at the product.

### Inventory

**`StockItem`** — `ProductId` (unique per tenant), `OnHand`, `ReorderPoint?`, `RowVersion`.

- `OnHand` is a **cached projection** of `StockMovement`, kept for fast reads. The ledger is the truth; `OnHand` can always be rebuilt from it.
- `RowVersion` maps to Postgres `xmin` for optimistic concurrency, so two registers selling the last unit collide loudly instead of silently.

**`StockMovement`** — append-only. `ProductId`, `Type` (`Receive`|`Adjust`|`Sale`|`Refund`|`Waste`|`Recount`), `Quantity` (signed), `Reason?`, `SaleId?`, `PerformedBy?`, `OccurredAt`.

- `Type` is stored as **text** with a check constraint generated from the enum's names, so a report or a psql session reads `Waste` rather than `4`. Renaming a member rewrites the constraint and orphans history — in a table that exists to be read back.
- `Quantity` is signed rather than a magnitude plus a direction implied by `Type`, so rebuilding on-hand is a `SUM` and not a fold that has to know what every type means.
- `Reason` is nullable in the column and **mandatory at `POST /stock/adjustments`**. A sale's reason is the sale; forcing prose onto it would produce a table full of the word "sale".
- `PerformedBy` is nullable (the system acts with no user) and deliberately **overlaps `CreatedBy`**. The actor is part of what the row *means* — a shrinkage investigation asks who wrote off the missing six — whereas the audit columns are metadata every table carries and may be reshaped for unrelated reasons.
- `OccurredAt` is separate from `CreatedAt` for the case Phase 9 creates: an offline movement is recorded when it happened and written when the till reconnects, and "what moved on Tuesday" means the first of those. Both are server-set, from `TimeProvider`.
- Written only through **`IStockLedger`** (declared in `Pos.Core`, implemented in `Pos.Data`), which appends the movement and applies it to `StockItem.OnHand` inside one transaction. That port is the one place the invariant below is enforced, and Phase 3's sale path joins it rather than reimplementing it.

**Why a ledger rather than a mutable count:** the question is never "what is on hand" — it is "why is on hand wrong?" A bare number cannot answer that, and shrinkage is a real business problem staff need to investigate. This also makes Phase 9's offline reconciliation possible: replaying queued movements against a ledger is tractable, reconciling two disagreeing counters is not.

### Sales

**`Sale`** — the financial record. **Append-only: once `Completed`, never updated or deleted.**

`SaleNumber` (per-tenant sequential, unique), `ClientTransactionId` (GUID, unique per tenant), `RegisterId`, `ShiftId`, `CashierId`, `Type` (`Sale`|`Refund`), `Status` (`Completed`|`Voided`), `OriginalSaleId?`, `Subtotal`, `DiscountTotal`, `TaxTotal`, `RoundingAdjustment`, `Total`, `TipAmount`, `CompletedAt`, `RecordedAt`, `VoidedAt?`, `VoidedBy?`, `VoidReason?`.

- **A refund is a new `Sale` with `Type = Refund`** and negative amounts, linked by `OriginalSaleId`. Never an edit of the original.
- **A void is a status flag plus compensating stock movements**, not a delete.
- `SaleNumber` is generated inside the sale's transaction from a per-tenant counter. Staff and auditors need a human-quotable reference; a GUID is not one. Gaps are suspicious in an audit, so the counter is not a naive `MAX()+1` read outside the transaction.
- **`CompletedAt` and `RecordedAt` are two different questions, and Phase 9 is why both are stored.** `CompletedAt` is when the customer stood at the counter — the client may supply it as `occurredAt` for a sale rung offline — and it is what every report, trading-day bound and Z-report groups by. `RecordedAt` is when the server wrote the row, always server-set, never client-supplied. They are equal for anything rung online, so the gap between them *is* the offline marker; nothing carries a flag a client would have to be trusted to set. Without the second column, a drawer counted at 18:00 that does not include a sale taken at 17:40 has nothing on the row to explain itself.

**`Barcode` carries a `DeletedAt`, and the removal is a soft delete.** Not for an audit trail — nothing financial points at a barcode — but for the offline mirror. A till syncs by asking what changed since it last looked, and a row deleted outright answers nothing, so the withdrawal would never reach it. Three things hold together or none of them works: the unique index on `(TenantId, Code)` is filtered on `DeletedAt IS NULL` so a code can be re-added, the scan and listing paths exclude tombstones, and `GET /catalog/sync` reports them as `removedBarcodeIds`.

**`SaleLine`** — `SaleId`, `ProductId`, `LineNumber`, `Description` *(snapshot)*, `Quantity`, `UnitPrice` *(snapshot)*, `TaxRate` *(snapshot)*, `DiscountAmount`, `LineSubtotal`, `LineTax`, `LineTotal`, `IsPriceOverridden`, `OverriddenBy?`.

**Snapshotting is the rule that matters here.** Description, unit price and tax rate are copied at sale time. Reports must never join to the current `Product.Price`. Otherwise raising a price on Tuesday retroactively rewrites Monday's revenue — the reports stop reconciling with the cash in the drawer, and there is no way to notice.

**`Tender`** — `SaleId`, `Method` (`Cash`|`Card`|`Voucher`|`External`), `Amount`, `ChangeGiven?`, `Reference?`.

- A **collection**, not a column: split payments are ordinary in retail.
- MVP implements `Cash` only (and `External` for a standalone card terminal where staff key in the amount). `Card` becomes a new row type in Phase 11 — the `Method` discriminator means that is additive, with no `Sale` schema migration.

### Shifts (cash reconciliation)

**`Shift`** — `RegisterId`, `OpenedBy`, `OpenedAt`, `OpeningFloat`, `ClosedBy?`, `ClosedAt?`, `CountedCash?`, `ExpectedCash?`, `Variance?`, `Status` (`Open`|`Closed`).

**`CashMovement`** — `ShiftId`, `Type` (`Drop`|`Payout`|`PettyCash`|`Correction`), `Amount`, `Reason`, `PerformedBy`, `OccurredAt`.

Without shifts, "the drawer is £12 short" is unanswerable. `Variance = CountedCash - ExpectedCash`, where expected = float + cash sales − cash refunds − drops/payouts. This is the backbone of the Z-report and the thing an owner actually checks daily.

### Restaurant (Phase 10)

**A separate model, not a bolt-on to `Sale`.** Every entity here is `TenantEntity`, and the link to money runs one way only: `OrderBill.SaleId` points at a `Sale`, and `Sale` gains no `OrderId`. The retail path compiles, queries and reports exactly as it did, unaware any of this exists.

**`Tenant.ServiceMode`** — `Retail` | `Restaurant`, defaulted in the column so every pre-existing shop is a counter. **Changeable, unlike `TaxMode`**: a tax mode decides what every stored price *means*; a service mode decides which screens a shop sees, and a `Sale` written in one reads identically in the other. Switching **to** `Retail` with orders still open is refused.

**`ServiceArea`** — `Name`, `SortOrder`, `IsActive`. A grouping, not a floor plan: no coordinates, no canvas.

**`DiningTable`** — `ServiceAreaId`, `Name`, `Seats`, `SortOrder`, `IsActive`. Table `dining_table`, because `TABLE` is a reserved word and this codebase writes raw SQL in the money paths. **It holds no "occupied" flag** — "is table 4 free?" is answered by asking whether an open `Order` points at it, which is one question with one answer; a boolean would be a second copy that disagrees the first time a request fails between updating one and the other. `Seats` is advisory and never enforced: six people sit at a four-top constantly.

**`Station`** — `Name` (unique per tenant), `SortOrder`, `IsActive`. A place in the kitchen that cooks things. A screen, not a person and not a printer — there is no printer support. A shop with one screen creates one station and every ticket lands on it, which is the correct degenerate case rather than a special one.

**Routing lives on `Category.StationId`**, inherited by sub-categories and by the products in them, with `Product.StationId` as the override. Both nullable, and `null` means "ask the level above", not "nowhere". `Pos.Core.Menus.StationRouting` owns the walk, is pure, and returns `null` for an item nothing routes — which the fire endpoint refuses by name rather than defaulting to a station.

**`Order`** — `OrderNumber`, `Type` (`Table`|`Tab`|`Takeaway`), `Status` (`Open`|`Closed`|`Abandoned`), `DiningTableId?`, `TabName?`, `RegisterId?`, `OpenedBy`, `OpenedAt`, `CoverCount?`, `Note?`, `ClosedAt?`, `ClosedBy?`, `AbandonReason?`. Table `customer_order`, for the reason above.

- **Working state, not a financial record.** Mutable for as long as it is open — lines added over an hour, quantities changed, items voided, the whole thing moved to another table. None of that is allowed of a `Sale` and none of it needs to be, so invariant 4 is untouched rather than weakened.
- **`OrderNumber` has its own per-tenant counter**, `OrderSequence`, separate from `SaleSequence`. One order can settle as three sales and an abandoned one settles as none, so sharing would scatter unexplained gaps through the financial series — the one thing that counter exists to avoid.
- **At most one open order per table**, enforced by a **filtered** unique index `ux_customer_order_tenant_table_open` on `(tenant_id, dining_table_id) WHERE status = 'Open' AND dining_table_id IS NOT NULL`. Not a pre-check: two staff seating one table in the same second both pass "is anything open here?".
- **`RegisterId` is informational** and deliberately not what the eventual sale is booked to. The money goes through whichever register takes the payment, because that is where the cash physically is.

**`OrderLine`** — `OrderId`, `ProductId`, `LineNumber`, `ParentOrderLineId?`, `Description`, `Quantity`, `UnitPrice`, `TaxRate`, `DiscountAmount`, `IsPriceOverridden`, `OverriddenBy?`, `Course`, `SeatNumber?`, `Note?`, `Status` (`Pending`|`Fired`|`Voided`), `FiredAt?`, `VoidedAt?`, `VoidedBy?`, `VoidReason?`.

- **It stores inputs, not amounts.** There is no `LineTotal`, unlike `SaleLine`, and the absence is deliberate: the money comes from `PricingEngine` whenever a bill is quoted or settled, from exactly these fields. A stored total is a second set of numbers to keep in step through every edit, void and re-split.
- **Snapshots are taken when the item is ordered**, not when the bill is paid — invariant 5, one entity earlier.
- `LineNumber` is allocated under a `FOR UPDATE` on the order row, from `MAX` rather than a count, so a void never hands its number to the next item.
- `ParentOrderLineId` is what makes a modifier one level deep and what makes "remove the burger" take its modifiers with it. A modifier's own `Course` and `SeatNumber` are not consulted anywhere: firing and billing both group by the parent.

**`ModifierGroup`** — `Name`, `MinSelections`, `MaxSelections?`, `SortOrder`, `IsActive`. **`ModifierOption`** — `ModifierGroupId`, `ProductId`, `SortOrder`, `IsDefault`. **`ProductModifierGroup`** — the many-to-many, with its own `SortOrder` because the ordering is a property of the pairing.

- **A modifier *is* a product** (`Product.IsModifier`), so it gets a price, a tax class and optional stock tracking for free and prices through the same engine. The flag keeps it out of the register grid, the product list and the offline mirror.
- **`ModifierOption` holds no price.** The price is the product's, because the product is what ends up on the order line and on the bill. "Free" is a product priced at zero.

**`KitchenTicket`** — append-only. `OrderId`, `StationId`, `Course`, `OrderNumber`, `OrderLabel`, `FiredAt`, `FiredBy`, `Status` (`Active`|`Bumped`), `BumpedAt?`, `BumpedBy?`. **`KitchenTicketLine`** — `KitchenTicketId`, `OrderLineId`, `LineNumber`, `Description`, `Quantity`, `SeatNumber?`, `ModifierText?`, `Note?`.

- **A record of an instruction, not a view over the order.** Everything a kitchen reads is snapshotted and none of it is refreshed. A line voided after firing is food that was already cooked; a ticket that re-read the order would quietly erase the evidence that the shop lost a steak. The void is shown *against* the ticket — the API's `isVoided`, read from the order line's current status — never edited into it.
- **One ticket per station per course per fire.** A single ticket holding the whole table is useless to a kitchen; a ticket spanning two courses is a queue the pass cannot pace.
- **`OrderNumber` and `OrderLabel` are snapshots**, so a display that polls every few seconds does not join per ticket per poll, and a table renamed at midnight does not rewrite what the grill was told at eight.
- **`ModifierText` is composed at firing** rather than stored as child rows. On a ticket a modifier is a phrase under the item, not a thing with a price, and rows would be a second parent/child tree to keep in step with the first for a reader that immediately flattens it.
- **There is no `Voided` status**, and `ck_kitchen_ticket_bumped_consistent` enforces that a ticket is either cleared with a name and a time against it or not cleared at all — the display's elapsed timer reads the pair.
- **Nothing here is money**: no prices, no totals, no tax. A kitchen does not charge anybody.

**`OrderBill`** — `OrderId`, `BillNumber`, `ClientTransactionId`, `SaleId?`, `PaidAt?`. **`OrderBillLine`** — `OrderBillId`, `OrderLineId`, `Quantity`.

- **Allocation is by quantity**, because a bottle is shared. Each order line's allocated quantities are validated to sum **exactly** to its quantity — a residue is refused, not absorbed.
- **The bill's `ClientTransactionId` and `Idempotency-Key` are minted with the bill, not with the payment.** Invariant 6: a key per attempt makes the header decorative and charges the table twice on a retry.
- **Splitting evenly is a tender split**, not a bill split: N cash tenders against one sale. Bills exist for splitting by item or by seat.

**`Sale.TipAmount`** — `ChangeGiven` becomes `tendered − total − tip`. **DATA-MODEL invariant 2 is amended** to `sum(Tender.Amount) >= Sale.Total + TipAmount`. The tip comes out of the change and never into the total: folding it in would inflate revenue, inflate the tax owed on revenue nobody was charged tax for, and make a refund of a meal offer to hand the gratuity back. `ShiftArithmetic` needs no change, because a tip reduces change given and is therefore already inside net cash tendered.

### Audit

**`AuditEntry`** — append-only. `Action`, `EntityType`, `EntityId`, `Before?` (jsonb), `After?` (jsonb), `ActorId`, `OccurredAt`, `RegisterId?`.

Records the actions that cost money or hide theft: price override, discount, void, refund, receipt issue, stock adjust, employee create/deactivate, role change, PIN reset, device enrol/revoke, settings change, shift close, and a refused adjustment attempt. Not a change-log of everything — a targeted record of sensitive actions, so it stays readable enough that someone will read it.

- **Append-only at the database, not by convention.** The app's role `pos_app` holds no `UPDATE` or `DELETE` grant on this table. That had to be revoked explicitly: the Phase 1.6 RLS migration grants both on every *future* table by default privileges, so the table was created with them.
- **`Action` is text via `HasEnumAsText`**, so the member names are the values in `ck_audit_entry_action_allowed` and in every row already written. Renaming one rewrites the constraint and orphans history — treat them as a wire contract.
- **`EntityType` is bounded text, not an enum.** It is polymorphic, and auditing a new kind of row should not mean rewriting a check constraint over history.
- **`Before`/`After` are a flat `Dictionary<string, string?>` serialised to `jsonb`** — not free-form JSON. It needs no Npgsql dynamic-JSON opt-in, it describes cleanly in OpenAPI so the generated client gets a usable type, and the read screen is a two-column table. Numbers are formatted with `InvariantCulture`: `InvariantGlobalization` is off, so a machine under a comma-decimal culture would otherwise write `"1,20"` into a permanent record.
- **`ActorId` has no foreign key**, matching `Sale.CashierId`, `SaleLine.OverriddenBy`, `StockMovement.PerformedBy` and `Shift.OpenedBy` — one would need an alternate key on Identity's user table that nothing else asks for. It is always the session's own user, even when a manager's grant permitted the action; the approver goes in `After`, so "who did this" means the same thing on every row.
- **`RegisterId` carries the tenant in its foreign key**, like every other tenant-scoped relationship. It is null for anything done from a back-office browser, because only PIN sessions and device tokens carry a `register_id` claim.
- **`OccurredAt` is separate from `CreatedAt`**, for the reason `StockMovement.OccurredAt` is: Phase 9 will record an action when it happened and write it when the till reconnects, and every question asked of this log means the first of those.

### Idempotency

**`IdempotencyRecord`** — `Key` (client GUID, unique per tenant), `Endpoint`, `RequestHash`, `ResponseStatus`, `ResponseBody`, `CreatedAt`.

Contract for every money- or stock-moving write:

1. Client generates a GUID **before** the first attempt and reuses it for every retry.
2. Server, in the same transaction as the work: insert the key or detect the conflict.
3. Key already present with a matching request hash → return the **stored original response**, same status. No second sale.
4. Key present with a *different* request hash → `409 Conflict`. The same key was reused for different content, which is surfaced loudly rather than absorbed: answering with the stored response would show a till a sale that succeeded, for a basket the customer never had.

**The hash is over the raw request body**, not a re-serialisation of the bound DTO — a client that changed a field this server currently ignores has still changed the request. It requires `EnableBuffering()` in `Program.cs` plus a rewind in the filter, because minimal-API endpoint filters run *after* model binding: without them every fingerprint would be computed over zero bytes, every key would look like a match, and the system would replay a stored response for an unrelated request. That failure is silent, which is why the differing-body test was written first.

**A `409` here is also information, not only a bug.** It means an earlier attempt with that key *landed*. A client that reaches it should find out what the key bought — `GET /sales/by-client-transaction/{id}` — rather than retrying, which will fail identically for ever. The corollary is that a key is bound to one request body: if the content legitimately changes, that is new work and needs a new key.

**Why from day one:** a cashier double-tapping on a slow connection must not charge a customer twice, and the offline outbox in Phase 9 is nothing more than this contract plus a retry loop. Retrofitting it later means revisiting every write path.

---

## Relationship overview

```
Tenant ─┬─ ApplicationUser ── RefreshToken
        ├─ Register ── Shift ─┬─ CashMovement
        │                     └─ Sale ─┬─ SaleLine ── (Product, snapshot)
        │                              ├─ Tender
        │                              └─ StockMovement
        ├─ Category ── Product ─┬─ Barcode
        │                       ├─ StockItem
        │                       └─ StockMovement
        ├─ TaxClass ── Product
        ├─ AuditEntry
        └─ IdempotencyRecord
```

## Foreign keys carry the tenant

Every relationship between tenant-owned rows is a **composite** foreign key —
`(tenant_id, product_id) → product(tenant_id, id)` — pointing at an alternate key
`ak_<table>_tenant_id_id`, not at the primary key.

**Postgres exempts referential integrity checks from row-level security.** A single-column
key on `product_id` would let one tenant reference another tenant's row and the check would
accept it, RLS notwithstanding. The tenant has to be *inside* the key. Deletes are
`RESTRICT`, never `CASCADE`. Rationale in [`DECISIONS.md`](../DECISIONS.md#resolved-2026-08-01-during-phase-2).

## Key indexes

| Table | Index | Reason |
|---|---|---|
| `category`, `tax_class`, `product` | unique `(tenant_id, id)` — `ak_*_tenant_id_id` | The principal key tenant-scoped foreign keys point at; also the index every by-id read wants, which a PK on `id` alone cannot serve |
| `tax_class` | unique `(tenant_id)` where `is_default` | At most one default per tenant — two would price new products nondeterministically |
| `barcode` | unique `(tenant_id, code)` | Hottest read in the app; every scan |
| `product` | unique `(tenant_id, sku)` | Business identity |
| `product` | `(tenant_id, name)` btree — `ix_product_tenant_name` | Ordering. `GET /products` sorts by `(name, id)` and pages by keyset, so this is what the cursor seeks into. GIN cannot serve `ORDER BY`, which is why it is not merged with the row below |
| `product` | `(tenant_id, name gin_trgm_ops)` **GIN** — `ix_product_tenant_name_trgm` | Search. `?q=` is a case-insensitive *contains* match and `ILIKE '%q%'` cannot use a btree at any width. Needs `pg_trgm`, and `btree_gin` for the leading `tenant_id` — without the second extension the tenant cannot be in the index and it would fail the leads-with-tenant rule |
| `category`, `tax_class` | `(tenant_id, name)` | Ordering, for the same keyset reason. `category.sort_order` is what a client arranges its picker by, but it is not unique so it cannot be a cursor's sort key |
| `sale` | unique `(tenant_id, client_transaction_id)` | The idempotency guarantee, enforced by the DB rather than a race-prone check-then-insert |
| `sale` | unique `(tenant_id, sale_number)` | Human reference |
| `sale` | `(tenant_id, completed_at)` | Reporting date ranges |
| `sale_line` | `(tenant_id, sale_id)` | Sale detail load |
| `stock_movement` | `(tenant_id, product_id, occurred_at)` | Ledger replay / rebuild `OnHand` |
| `stock_item` | unique `(tenant_id, product_id)` | One stock row per product |
| `audit_entry` | `(tenant_id, occurred_at)` | Audit review |
| `barcode` | unique `(tenant_id, code)` **filtered** `WHERE deleted_at IS NULL` | The hottest read in the system, and the filter is what lets a withdrawn code be entered again — a mis-typed label being corrected is the ordinary case, and an unfiltered index answers it with a `23505` for a code the shop cannot see anywhere |
| `barcode` | `(tenant_id, deleted_at)` filtered `WHERE deleted_at IS NOT NULL` | "Which codes were withdrawn since?" — a small list on every sync, and a scan of every barcode the shop has without it |
| `product`, `barcode`, `tax_class`, `category` | **expression** `(tenant_id, COALESCE(updated_at, created_at), id)` — `ix_*_tenant_changed` | The `GET /catalog/sync` keyset. `COALESCE` because `updated_at` is null until a row is first edited, so ordering on it alone sorts every never-edited row into one null bucket a keyset cannot page through. An expression index is the only kind that serves an `ORDER BY` over it — hand-written in the migration, since EF has no API for one |

## Invariants

These hold in every phase. A change request that breaks one is a design discussion, not a patch.

1. `Sale.Total == Subtotal - DiscountTotal + TaxTotal + RoundingAdjustment` (with `TaxMode = Inclusive`, tax is extracted from rather than added to the line prices, and this identity still holds).
2. `sum(Tender.Amount) >= Sale.Total + Sale.TipAmount` for a cash sale; the excess is `ChangeGiven`. **Amended in Phase 10.6, and the amendment is the point rather than a detail.** The product takes cash only, so a tip is physically cash left in the drawer: €25 against a €20 bill with a €5 tip is nothing back, not €5 of change. Taking the tip out of the change instead of adding it to the total is what leaves the money inside `ExpectedCash` — which sums tendered less change given — so `ShiftArithmetic` needs no change at all. The tip is deliberately **not** part of `Total`: folding it in would inflate revenue, inflate the tax owed on revenue nobody was charged tax for, and make a refund of a meal offer to hand the gratuity back too.
3. `StockItem.OnHand == sum(StockMovement.Quantity)` for that product. Any drift is a bug, and is detectable precisely because the ledger exists.
4. A `Completed` sale is never mutated. Corrections are new linked rows.
5. Every row in a tenant table has a non-empty `TenantId`. No nulls, no sentinel "shared" tenant.
6. Historical amounts are never recomputed from current catalog data.
