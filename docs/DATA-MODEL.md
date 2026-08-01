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
`Id`, `Name`, `Slug` (unique), `IsActive`, `CurrencyCode`, `TimeZoneId`, `TaxMode` (`Inclusive`|`Exclusive`), `BusinessDayStartOffset`, `CreatedAt`.

- `TimeZoneId` + `BusinessDayStartOffset` are what let a shift closing at 02:00 land on the correct trading day.
- **`TaxMode` is set at onboarding and effectively immutable.** Flipping it reinterprets every stored price. EU retail quotes tax-inclusive shelf prices; US retail adds tax at the till. Getting this wrong is not a display bug, it is a wrong-price bug.

**`ApplicationUser`** — `IdentityUser<Guid>` + `TenantId`, `DisplayName`, `PinHash`, `IsActive`, `LastLoginAt`.

- Identity's default unique index on `NormalizedEmail` is **replaced** with composite `(TenantId, NormalizedEmail)`. Otherwise one person cannot be a user at two tenants, and onboarding fails with an opaque duplicate-key error.
- `PinHash` is unique per `(TenantId, PinHash)`? **No** — do not enforce that. It would let an attacker enumerate which PINs are taken. Collisions are resolved by the user picking the employee they are, or by PIN + employee selection.

**`Register`** — a physical till. `Name`, `DeviceTokenHash`, `IsActive`, `LastSeenAt`.

**`RefreshToken`** — `UserId`, `TokenHash`, `ExpiresAt`, `RevokedAt`, `ReplacedByTokenId`, `FamilyId`. Rotated on use; reuse of a rotated token revokes the whole `FamilyId`.

### Catalog

**`Category`** — `Name`, `ParentCategoryId?`, `SortOrder`, `IsActive`.

**`TaxClass`** — `Name`, `Rate` (`numeric(6,4)` — 0.2000 = 20%), `IsDefault`.
A class, not a bare rate on the product: rates change by law, and a rate change must not require touching every product. Historical sales keep their snapshotted rate regardless.

**`Product`** — `Sku` (unique per tenant), `Name`, `Description`, `CategoryId?`, `TaxClassId`, `UnitPrice`, `CostPrice?`, `Unit` (`Each`|`Kilogram`|`Litre`), `IsActive`, `TrackStock`.

- **Never hard-deleted.** Sale lines reference products forever; a delete either orphans history or cascades away a customer's sales records.
- `CostPrice` drives margin reporting and is gated behind `CanViewMargins`.
- `TrackStock = false` for services and open-price items.

**`Barcode`** — `ProductId`, `Code`, `IsPrimary`.

- **Many per product** — multipacks, re-labelled stock, supplier variations, and the same item scanning differently across two suppliers. One-barcode-per-product is the single most common retail catalog modelling mistake.
- Unique index on `(TenantId, Code)`, and this is the hottest read in the system.

### Inventory

**`StockItem`** — `ProductId` (unique per tenant), `OnHand`, `ReorderPoint?`, `RowVersion`.

- `OnHand` is a **cached projection** of `StockMovement`, kept for fast reads. The ledger is the truth; `OnHand` can always be rebuilt from it.
- `RowVersion` maps to Postgres `xmin` for optimistic concurrency, so two registers selling the last unit collide loudly instead of silently.

**`StockMovement`** — append-only. `ProductId`, `Type` (`Receive`|`Adjust`|`Sale`|`Refund`|`Waste`|`Recount`), `Quantity` (signed), `Reason?`, `SaleId?`, `PerformedBy`, `OccurredAt`.

**Why a ledger rather than a mutable count:** the question is never "what is on hand" — it is "why is on hand wrong?" A bare number cannot answer that, and shrinkage is a real business problem staff need to investigate. This also makes Phase 9's offline reconciliation possible: replaying queued movements against a ledger is tractable, reconciling two disagreeing counters is not.

### Sales

**`Sale`** — the financial record. **Append-only: once `Completed`, never updated or deleted.**

`SaleNumber` (per-tenant sequential, unique), `ClientTransactionId` (GUID, unique per tenant), `RegisterId`, `ShiftId`, `CashierId`, `Type` (`Sale`|`Refund`), `Status` (`Completed`|`Voided`), `OriginalSaleId?`, `Subtotal`, `DiscountTotal`, `TaxTotal`, `RoundingAdjustment`, `Total`, `CompletedAt`, `VoidedAt?`, `VoidedBy?`, `VoidReason?`.

- **A refund is a new `Sale` with `Type = Refund`** and negative amounts, linked by `OriginalSaleId`. Never an edit of the original.
- **A void is a status flag plus compensating stock movements**, not a delete.
- `SaleNumber` is generated inside the sale's transaction from a per-tenant counter. Staff and auditors need a human-quotable reference; a GUID is not one. Gaps are suspicious in an audit, so the counter is not a naive `MAX()+1` read outside the transaction.

**`SaleLine`** — `SaleId`, `ProductId`, `LineNumber`, `Description` *(snapshot)*, `Quantity`, `UnitPrice` *(snapshot)*, `TaxRate` *(snapshot)*, `DiscountAmount`, `LineSubtotal`, `LineTax`, `LineTotal`, `IsPriceOverridden`, `OverriddenBy?`.

**Snapshotting is the rule that matters here.** Description, unit price and tax rate are copied at sale time. Reports must never join to the current `Product.Price`. Otherwise raising a price on Tuesday retroactively rewrites Monday's revenue — the reports stop reconciling with the cash in the drawer, and there is no way to notice.

**`Tender`** — `SaleId`, `Method` (`Cash`|`Card`|`Voucher`|`External`), `Amount`, `ChangeGiven?`, `Reference?`.

- A **collection**, not a column: split payments are ordinary in retail.
- MVP implements `Cash` only (and `External` for a standalone card terminal where staff key in the amount). `Card` becomes a new row type in Phase 11 — the `Method` discriminator means that is additive, with no `Sale` schema migration.

### Shifts (cash reconciliation)

**`Shift`** — `RegisterId`, `OpenedBy`, `OpenedAt`, `OpeningFloat`, `ClosedBy?`, `ClosedAt?`, `CountedCash?`, `ExpectedCash?`, `Variance?`, `Status` (`Open`|`Closed`).

**`CashMovement`** — `ShiftId`, `Type` (`Drop`|`Payout`|`PettyCash`|`Correction`), `Amount`, `Reason`, `PerformedBy`, `OccurredAt`.

Without shifts, "the drawer is £12 short" is unanswerable. `Variance = CountedCash - ExpectedCash`, where expected = float + cash sales − cash refunds − drops/payouts. This is the backbone of the Z-report and the thing an owner actually checks daily.

### Audit

**`AuditEntry`** — append-only. `Action`, `EntityType`, `EntityId`, `Before?` (jsonb), `After?` (jsonb), `ActorId`, `OccurredAt`, `RegisterId?`.

Records the actions that cost money or hide theft: price override, discount, void, refund, stock adjust, role change, PIN reset. Not a change-log of everything — a targeted record of sensitive actions, so it stays readable enough that someone will read it.

### Idempotency

**`IdempotencyRecord`** — `Key` (client GUID, unique per tenant), `Endpoint`, `RequestHash`, `ResponseStatus`, `ResponseBody`, `CreatedAt`.

Contract for every money- or stock-moving write:

1. Client generates a GUID **before** the first attempt and reuses it for every retry.
2. Server, in the same transaction as the work: insert the key or detect the conflict.
3. Key already present with a matching request hash → return the **stored original response**, same status. No second sale.
4. Key present with a *different* request hash → `409 Conflict`. The same key was reused for different content, which is a client bug worth surfacing loudly.

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

## Invariants

These hold in every phase. A change request that breaks one is a design discussion, not a patch.

1. `Sale.Total == Subtotal - DiscountTotal + TaxTotal + RoundingAdjustment` (with `TaxMode = Inclusive`, tax is extracted from rather than added to the line prices, and this identity still holds).
2. `sum(Tender.Amount) >= Sale.Total` for a cash sale; the excess is `ChangeGiven`.
3. `StockItem.OnHand == sum(StockMovement.Quantity)` for that product. Any drift is a bug, and is detectable precisely because the ledger exists.
4. A `Completed` sale is never mutated. Corrections are new linked rows.
5. Every row in a tenant table has a non-empty `TenantId`. No nulls, no sentinel "shared" tenant.
6. Historical amounts are never recomputed from current catalog data.
