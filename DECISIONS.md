# POS Project — Locked-In Decisions

This file is the source of truth for architecture/product decisions. Read this first at the start of the next session, then [`docs/ROADMAP.md`](docs/ROADMAP.md) for current progress.

> **Updated 2026-07-30**: four of the five open questions are now resolved (database, tenant isolation, payments-for-MVP, auth) and folded into the sections below. The full phase/milestone plan now lives in [`docs/`](docs/ROADMAP.md).

## Product

- **What we're building**: a POS (point of sale) system, aiming to be flexible enough for both retail and restaurant workflows ("universal"), though retail is the build-first target.
- **Business model**: sell as a whole product, not a per-seat license. Leaning toward one-time purchase, but this is in tension with the hosting decision below — still unresolved, see [Open Questions](#still-unresolved).
- **Sequencing**: **web (online) version first**, desktop version later, sharing the same backend.

## Hosting & Multi-tenancy

- **We (the vendor) host the web version** — SaaS-style. All clients' data lives on our infrastructure.
- This means the backend must be **multi-tenant** from the start: every table/entity scoped by `TenantId`, with strict data isolation between clients.
- **Isolation strategy (resolved 2026-07-30): shared database with a `TenantId` column.** Not database-per-tenant or schema-per-tenant. Cheapest to run and to migrate (one migration serves every tenant), which matters most pre-first-customer. Isolation is enforced in three redundant layers rather than by discipline: EF Core global query filters applied to every tenant entity *by reflection* (so a new entity can't be forgotten), a `SaveChanges` interceptor that stamps `TenantId` and rejects cross-tenant writes, and Postgres row-level security as the layer that holds when application code is wrong (query filters don't apply to raw SQL). Revisit if an enterprise deal demands physical separation. Mechanics: [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#multi-tenancy).

## Offline Requirements

- **Checkout must survive offline** — both for the eventual desktop app and for the web app. This was identified as the single most common reason small businesses reject a POS ("system went down during rush hour").
- **Web offline strategy**: PWA (service worker + IndexedDB), queueing transactions locally and syncing when connectivity returns.
  - Known hard part: conflict resolution (e.g. two registers selling the last unit of stock while both offline). Plan: idempotent client-generated transaction IDs (GUIDs) to prevent duplicate submission on sync, and an explicit reconciliation rule for oversold inventory (flag for staff review rather than silently corrupting stock counts).
  - Browser storage is less durable than a native local DB (can be cleared, Safari is stricter) — treat as "best effort" for v1, not bulletproof. Full offline reliability is expected to land properly with the desktop app.
- **Recommended sequencing**: build the web app **online-only first** to ship a real, working product; layer in offline queueing once core flows (checkout, inventory, reporting) are proven. Don't try to build the product and offline-sync simultaneously.

## Tech Stack

- **Backend**: ASP.NET Core Web API (C#). Developer is comfortable in C#; this was chosen over Node/Electron partly because the POS hardware ecosystem (receipt printers, cash drawers, scanners) has strong native .NET SDK/OPOS driver support, which matters once desktop peripherals enter the picture.
- **Web frontend**: separate JS/TS frontend (React or similar), built as a PWA. Chosen over Blazor for the bigger UI ecosystem/talent pool, accepting the cost of working in two languages.
- **Desktop app (later)**: C# + **Avalonia UI** (cross-platform XAML, Windows + Mac). Chosen over Electron for lower resource footprint and stronger native peripheral/vendor SDK support; chosen over WPF because WPF is Windows-only and Mac support is required. Chosen over .NET MAUI because MAUI's desktop story (esp. Mac Catalyst) is less proven for dense, complex UIs like a checkout screen.
- **Database (resolved 2026-07-30): PostgreSQL 17**, accessed via EF Core + Npgsql. Chosen over SQL Server because we host every tenant ourselves, so SQL Server's per-core licensing comes straight out of margin; managed Postgres is cheaper at small scale; and Postgres has native row-level security, which gives us a second isolation layer for free.
- **Auth (resolved 2026-07-30): ASP.NET Core Identity + JWT**, self-hosted rather than an external IdP (Clerk/Auth0). No per-MAU cost on a product we sell as a whole, full control of the tenant→user→role model, and no external dependency inside the offline story. Two-tier login, because a register is a shared device and a cashier cannot type an email and password between customers: email/password for Owner/Manager and for enrolling a device, then a hashed 4–6 digit PIN for cashiers **on an already-enrolled register only**. A PIN alone is never sufficient — four digits is 10,000 possibilities, so the device token and lockout are both load-bearing.
- **Frontend specifics**: Vite + React + TypeScript + Tailwind + shadcn/ui, as a pure SPA. Chosen over Next.js because a POS needs no SSR, and server rendering fights the service-worker/offline model we need in Phase 9.
- **Testing**: xUnit on `Core`, integration tests via `WebApplicationFactory` + Testcontainers Postgres on `Api` (including explicit cross-tenant isolation tests), Vitest + Playwright on the web app. Chosen deliberately: this system handles money, and pricing/rounding/tender bugs are invisible until a customer disputes a receipt.

## Solution Structure

`YourPOS.*` was a placeholder; the concrete naming is `Pos.*`. **Rename now if the product has a real name** — it is a wide diff later.

```
Pos.sln
├─ src/
│  ├─ Pos.Core      — business logic (pricing, tax, discounts, tender, stock rules). Pure C#, no DB/UI/HTTP.
│  ├─ Pos.Data      — EF Core: DbContext, configurations, tenant interceptors, migrations.
│  ├─ Pos.Api       — ASP.NET Core Web API. The one backend both web and (later) desktop clients call.
│  ├─ Pos.Web       — React/TS PWA frontend, talks to Api.
│  └─ Pos.Desktop   — (later) Avalonia app. Talks to the SAME Api; adds a local offline cache/sync layer
│                     rather than being a fully separate offline silo.
└─ tests/
   ├─ Pos.Core.Tests — pure and fast, no DB
   ├─ Pos.Data.Tests — EF behaviour: filters, interceptors, migrations
   └─ Pos.Api.Tests  — end-to-end through HTTP, incl. the cross-tenant isolation suite
```

Rationale: `Core` and `Data` are shared, unmodified, between web and desktop — this is what makes "same backend, two clients" real rather than aspirational. Only the UI/hosting layer differs per client.

This is only real if the dependency rule holds: **`Pos.Core` references nothing outside the BCL.** Enforced by an architecture test rather than by convention, because conventions decay and the test doesn't. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md#dependency-rule).

## Admin / Roles

Two distinct "admin" concepts — do not conflate them:

1. **Store admin/owner role** (inside each tenant's POS) — role-based access control (`Cashier`, `Manager`, `Owner`) distinguishing who can change prices, view margins, issue refunds, manage employees, etc. **This is core MVP functionality**, not optional — build it from the start as part of `Core`/`Api`.
2. **Platform admin** (for us, the vendor) — a separate internal tool/dashboard to manage all tenants: onboarding, billing/payment status, active/inactive status, impersonate-for-support. **Decision: not worth building yet.** Build it right after the first paying client, once real operational needs are known. Before that, direct DB inspection is an acceptable stopgap for a handful of tenants.

## Feature Roadmap (phased)

1. **MVP — retail core**: product catalog, barcode/manual entry, cart, checkout (**cash only** — see below), receipt printing, inventory decrement, daily sales report, store roles.
2. **V2 — restaurant mode**: tables/tabs, kitchen printer routing, item modifiers, split bills, tipping. Retail and restaurant have genuinely different data models (simple "sale" vs. "order" with courses/seats/modifiers) — do not force one schema for both prematurely; extend once retail is proven.
3. **V3 — business layer**: customer/loyalty, purchase orders, low-stock alerts, multi-register on one tenant. (Employee accounts/roles have moved *into* the MVP — see the Admin section: a shop with more than one employee cannot use a POS where everyone can change prices and issue refunds.)
4. **V4 — nice-to-haves**: gift cards, analytics dashboard, platform admin (see above), license/activation considerations if the one-time-sale model is kept.

**Payments (resolved 2026-07-30, narrowed 2026-07-31): the product takes cash only, and card processing is out of scope — not deferred.** The original decision cut card processing from the *MVP* and parked it in Phase 11. That is now a decision about the product, not the phase: the shop this is being built for takes cash from customers across a counter, so there is no processor to integrate, no PCI surface, and no payment hardware. **Phase 11 is dropped from the plan** and the payment-processor question is closed rather than deferred.

What this does *not* change: `Tender` stays a **collection of rows with a `Method` discriminator**, not an amount column on `Sale`. That shape is load-bearing for cash alone — split tender and change due need it — and it is what keeps a future card terminal from being a `Sale` schema migration. `External` (a standalone terminal where staff key in the amount, reconciled in the Z-report) stays a documented `Method` value for that reason, but **the MVP implements `Cash` only** and nothing is built against `External` until a shop actually has a terminal. Reopening this means writing an `IPaymentProvider` port from scratch, which is the correct cost — building the port now for a processor that may never exist is speculative work with a real maintenance surface.

The mapping from this roadmap to executable phases is [`docs/ROADMAP.md`](docs/ROADMAP.md): MVP = Phases 0–8, offline = Phase 9, restaurant mode = Phase 10.

## Open Questions

### Still unresolved

- **Pricing model conflict**: "sell as a whole, not a license" (one-time) vs. "we host it" (implies ongoing infra cost). Needs a deliberate decision — e.g. one-time fee that includes lifetime hosting, or accept a recurring fee without calling it a "license."
  **Not blocking**: no billing or licensing code exists in the MVP, so this can stay open until Phase 10. It does need deciding before a price is quoted to a real customer, because "lifetime hosting for a one-time fee" is a liability that grows with every tenant.

### Deferred to the phase that decides them

- **Hosting provider** — Fly.io vs. Azure App Service vs. VPS, all viable. Decided and recorded in [Phase 8.2](docs/phases/PHASE-8-deployment.md).
- ~~**Payment processor**~~ → **closed 2026-07-31: there isn't one.** The product takes cash only; see [Payments](#feature-roadmap-phased) above. Not "Stripe, later" — no processor is planned at all.

### Resolved 2026-08-01 (during Phase 2.3 and 2.4)

- **One primary barcode per product is *not* enforced.** `Barcode.IsPrimary` is advisory: a
  label/display hint, while the register scans whichever code is on the item in the customer's
  hand. The alternative was a filtered unique index on `(tenant_id, product_id) WHERE
  is_primary`, which has two real costs — EF would treat the foreign-key index as covered, and
  "make this the label code" becomes a clear-then-set across two `SaveChanges` inside a
  transaction, exactly the shape `TaxClassEndpoints.SaveWithDefaultAsync` had to grow. Paying
  that for a rule nothing reads yet is not worth it. Two primaries on one product is therefore
  possible and harmless; the screen that eventually shows a label code picks one. Pinned by
  `BarcodeTests.A_primary_flag_is_stored_as_sent_and_nothing_polices_it`, so adding the
  constraint later is a deliberate reversal rather than a tidy-up.

- **A barcode is trimmed but never case-folded**, unlike a SKU. The asymmetry is deliberate: a
  SKU is a human-typed identifier where `abc-1` and `ABC-1` must be one product, whereas a
  barcode is whatever the symbology encodes, and GS1-128 application identifiers carry
  case-significant data. Upper-casing a scan would silently change what the label says.

- **`POST /stock/adjustments` is not idempotent, and the 🔒 has been removed from `API.md`
  until it is.** `IdempotencyRecord` is Phase 3.5's, built once for sales, voids, refunds and
  adjustments together — invariant 6 wants one mechanism, not a stock-shaped copy that 3.5
  then has to reconcile with the sale path. The cost is real and is recorded rather than
  discovered: a resubmitted adjustment writes a second movement. That is at least *visible* in
  an append-only ledger, which a silently lost one would not be.
  `StockAdjustmentTests.A_resubmitted_adjustment_writes_a_second_movement` pins today's
  behaviour and is the test 3.5 should break.

- **The stock ledger pages oldest-first, and `CursorPaging` stays ascending-only.** A
  descending mode (`EF.Functions.LessThan` + `OrderByDescending`) would touch the helper every
  list endpoint runs through, to serve a screen that does not exist until Phase 6. Oldest-first
  is also the order `ix_stock_movement_tenant_product_occurred` holds and the order a rebuild
  replays, so nothing is being worked around. Revisit in Phase 6 with a UI to argue from.

- **`GET /stock/discrepancies` is deferred to Phase 3.6.** Nothing can flag an oversell until
  the sale path exists, so the endpoint could only ever return an empty list — and an endpoint
  whose only possible response is empty cannot be meaningfully tested, which would make it
  worse than absent. It arrives with the phase that creates the flag.

- **`RebuildOnHand` has no HTTP route.** It lives on `IStockLedger` and is exercised by tests.
  Exposing "recompute the stock figures" needs a decision about who may run it, and how a
  tenant-wide rebuild interacts with concurrent trading, that Phase 2 does not need to take.

- **`IStockLedger` is the first persistence port `Pos.Core` declares**, and the precedent is
  narrow on purpose. It earns one because the operation is not a save: it is a transaction with
  a concurrency token in it, wrapped in an execution strategy, and Phase 3's sale path has to
  *join* that transaction rather than reimplement it. A port per repository is not being
  adopted — the endpoints still use `AppDbContext` directly, which is the existing pattern.

- **A lost stock race is a 409 and is never retried inside the ledger.** Re-applying a delta
  the caller may already have applied is indistinguishable, from inside, from "receive six"
  arriving twice. Phase 3.5's idempotency keys are what make an automatic retry safe; until
  then the decision belongs to whoever knows whether the stock physically moved.

- **The isolation manifest's `UrlFor` substitutes every route parameter and refuses to build a
  URL it cannot fill.** Previously it substituted only the first, which left a literal
  `{barcodeId:guid}` in the path — a request that reaches no endpoint and answers 404, which is
  the same 404 the by-id theory asserts. `EndpointCoverageTests` now also fails a by-id row with
  more than one parameter and no `VictimPath`. Both halves were verified by deliberately
  breaking the `DELETE` row. This closes trap 16 in the previous handoff structurally rather
  than by remembering it.

### Resolved 2026-08-01 (during Phase 2.2)

- **Case-insensitive search is `pg_trgm` + `btree_gin`, not a `lower()` expression index.**
  `?q=` is a *contains* match and `ILIKE '%q%'` cannot use a btree at any width, so the
  alternative was to quietly downgrade the contract to "starts with". `btree_gin` is not
  incidental: it supplies GIN an operator class for `uuid`, and without it `tenant_id` cannot
  be a column of the index at all — which would fail the rule that every index on a tenant
  table leads with the tenant. Both are *trusted* extensions in PG 13+, so `CREATE` on the
  database suffices and no superuser is involved; **Phase 8.2's host must allow both.**
  Verified by hand at 50,000 rows: the planner takes the index with both predicates as
  `Index Cond`. It does not at 3,000, which is correct — that is cost, not capability.

- **SKU is matched exactly, not by trigram.** Trigrams need three non-wildcard characters to
  be selective and SKUs are short, so a trigram index on `sku` would scan anyway while
  costing write amplification on every product write. `ux_product_tenant_sku` already serves
  the equality, and what staff do with a SKU is type or scan the whole code.

- **A cursor is a position, not a capability, and is not signed.** It carries no tenant and
  grants nothing: tenancy comes from the validated token, and the query filter plus RLS scope
  the query before the keyset predicate is reached — so replaying tenant A's cursor inside
  tenant B returns a legal page of B's own rows. An HMAC would buy key management in exchange
  for tamper-proofing a value whose worst tampered outcome is an empty page. Pinned by a
  test, so "hardening" it later is a deliberate act rather than a tidy-up.

- **A field a role may not read is a field it may not write, and the write is *ignored*, not
  rejected.** A Manager holds `CanManageCatalog` but not `CanViewMargins`; their `GET` omits
  `costPrice`, so the obvious read-modify-write sends it back absent — and `PUT` is a full
  replacement. Rejecting would break every Manager edit; honouring the absence would destroy
  the Owner's cost data on each one. Same shape as a `TenantId` in a request body.

- **`TaxClass.IsDefault` is clear-then-set, not a refusal.** "Make this the default" is what
  the caller means, and refusing a second default would force a two-call dance with a window
  in which the shop has none. The remaining race — two creates that both arrive with
  `isDefault` and find nothing to clear — is a `409`, because re-reading and retrying is a
  meaningful thing for the caller to do.

- **`POST /products/{id}/activate` and the category equivalent were added to the contract.**
  `docs/API.md` defined `deactivate` and nothing else, and `PUT` deliberately does not carry
  `isActive` (so that a price edit can never resurrect a row by accident) — which together
  made a mis-clicked deactivate permanent short of hand-written SQL.

- **Foreign keys carry the tenant.** Every relationship between tenant-owned rows is a
  *composite* key — `barcode(tenant_id, product_id) → product(tenant_id, id)` — pointing at
  an alternate key `ak_<table>_tenant_id_id` rather than at the primary key. Phase 2.1 is
  the first place the schema has foreign keys at all; `RefreshToken.UserId` is a bare `Guid`
  because nothing pointed at it.

  **The reason is specific and not obvious: Postgres exempts referential integrity checks
  from row-level security.** RI triggers run as the referenced table's owner with row
  security off, so a single-column key on `product_id` would let tenant B insert a barcode
  referencing tenant A's product and the check would *accept* it — the policy that hides the
  row does not apply to the check. RLS is not a backstop here; it is bypassed by design.
  Carrying `tenant_id` inside the key makes a cross-tenant reference a `23503` violation.
  Pinned by `CatalogForeignKeyTests`, which runs as `pos_app` for exactly that reason, and by
  `TenantModelTests.Every_tenant_scoped_relationship_carries_the_tenant_in_its_foreign_key`.

  Cost: one extra unique constraint per parent table. It is not waste — `(tenant_id, id)` is
  the index every by-id read wants once the query filter adds `tenant_id = @p`, which a
  primary key on `id` alone cannot serve.

- **`OnDelete` is `Restrict` everywhere in the catalog**, never `Cascade`. Products are
  withdrawn with `IsActive` and never deleted; a cascade would quietly take the barcodes,
  and later the sale lines, with the row.

- **`StockItem.RowVersion` maps to Postgres' `xmin`** rather than being a column we maintain.
  Note for whoever reads the Npgsql docs: `UseXminAsConcurrencyToken()` **no longer exists**
  in Npgsql 10 — the string `Xmin` is absent from the assembly. It was replaced by a
  convention that matches a property that is `uint`, generated on add *and* update, and a
  concurrency token. Changing any of those three silently turns it back into an ordinary
  column, which is why `StockItemConcurrencyTests` asserts both the mapping and that no
  `row_version` column exists.

### Resolved 2026-07-31 (during Phase 1)

Four gaps the planning docs left open. All four are load-bearing for tenant isolation; none should be revisited without reading the reasoning here first.

- **Login names the tenant.** `POST /auth/login` takes `{ tenantSlug, email, password }`. The server resolves the `tenant` row by slug — that table is the tenant list, so it carries no `TenantId`, no query filter and no RLS — sets the ambient tenant, and only then looks for a user.

  The alternative, which `PHASE-1.3` originally described, was to find the user first and read the tenant off their row. That forces `application_user` (and `refresh_token`, and `register`) permanently outside both the query filter and RLS — the three tables an attacker would most like to read across tenants. Naming the tenant first means **no table is an exception**, which is what makes "every tenant table is scoped" a statement a test can check.

  **This is not a breach of invariant 2.** A slug is a pre-authentication *selector*, not a `TenantId`: it only narrows the search, and the credentials still have to match a user inside that tenant. After login, `TenantId` comes from the validated token and nowhere else. Login returns one identical response and comparable timing for unknown slug, unknown email and wrong password, so it is not a tenant-enumeration oracle.

- **Every non-JWT token carries its tenant as a prefix**: `base64url(tenantId).base64url(32 random bytes)`. Applies to refresh tokens and register device tokens, both of which are presented when no tenant is known. Without the prefix, looking one up means querying across every tenant — the exact unscoped read this phase exists to prevent. The prefix is untrusted and only selects which tenant to search; the 256-bit half authenticates, matched by SHA-256 inside that tenant. A forged prefix lands the caller in a tenant where their hash matches nothing.

  Note the deliberate asymmetry in hashing: PINs and passwords use the slow salted password hasher because a human chose them and a 4-digit PIN has 10,000 values; device and refresh tokens use a plain SHA-256 because they are 256 random bits and a *deterministic* digest is required to look the row up at all.

- **Two Postgres roles.** `pos` owns the schema and runs migrations; `pos_app` is `NOBYPASSRLS` and is what the API connects as. `FORCE ROW LEVEL SECURITY` is set regardless. Role *creation* needs a password so it is a bootstrap step, not a migration — no secrets in committed SQL. The migration does the grants and `ALTER DEFAULT PRIVILEGES`, so tables added in later phases are covered without anyone remembering.

- **RLS uses session-level `set_config`, not `SET LOCAL`.** `PHASE-1.6` says `SET LOCAL`, which is wrong in a way that would have shipped silently: outside an explicit transaction it is scoped to the implicit single-statement transaction, and EF reads do not open transactions. RLS would have looked configured and enforced nothing. The setting is issued from a connection interceptor as `SELECT set_config('app.tenant_id', $1, false)` — parameterised, never interpolated. Safe with pooling because Npgsql sends `DISCARD ALL` on reset, so the connection string must **not** set `No Reset On Close=true` and multiplexing must stay off.

- **A validly signed access token is trusted for whatever tenant it names.** Nothing re-checks, per
  request, that the token's `sub` actually belongs to its `tenant_id`. So a token minted with another
  tenant's id and `role: Owner` reads that tenant's data on any path that does not itself load the
  user — `GET /registers` needs only the role claim, whereas `/auth/me` happens to catch the mismatch
  because it looks the subject up.

  **Accepted, and recorded rather than fixed.** Producing such a token requires the signing key, and
  anyone holding it can mint any claim they like — so this is not reachable from outside. What it
  means is that **the signing key is the tenancy boundary**, and it should be read alongside the three
  layers, which all sit *below* it: they scope a request to the tenant the token claims, and none of
  them second-guesses the claim.

  Closing it means an `OnTokenValidated` that confirms the subject exists in the claimed tenant and is
  still active — one indexed lookup per request, which would also stop a deactivated user's token
  working for the remainder of its 15 minutes. Worth doing when there is a reason to (a key rotation
  story, or staff turnover complaints); not worth the per-request cost purely on the strength of a
  threat that starts with "the signing key leaked". Pinned by
  `ForgedTenancyTests.A_validly_signed_token_is_trusted_for_whatever_tenant_it_names`, which goes red
  if anyone changes it — deliberately, so the change is a decision and not a surprise.

### Resolved 2026-07-30

- ~~Postgres vs. SQL Server~~ → **Postgres** (see Tech Stack).
- ~~Multi-tenant isolation strategy~~ → **shared DB + `TenantId`**, three enforcement layers (see Hosting & Multi-tenancy).
- ~~Payment processor for MVP~~ → **cash only**; narrowed further on 2026-07-31 from "deferred to Phase 11" to "out of scope" (see Feature Roadmap).
- Auth approach → **ASP.NET Core Identity + JWT** with two-tier credential/PIN login (see Tech Stack).

## Two decisions that are not retrofittable

Called out because they are cheap now and expensive-to-impossible later:

- **`TaxMode` per tenant** (`Inclusive` for EU-style shelf pricing, `Exclusive` for US-style add-at-till). It determines how every stored price is interpreted, so changing it after trading silently rewrites history. Set at onboarding, refused thereafter. Decided in [Phase 3.2](docs/phases/PHASE-3-checkout-sales.md).
- **Idempotency keys on every money- and stock-moving write**, from Phase 3 rather than alongside the offline work. It prevents double-charging a customer on a double-tap *today*, and it is the entire mechanism the Phase 9 offline outbox is built on — meaning offline sync needs no separate API and no second server-side write path.

## Current State

**Phases 0 and 1 are complete**, on branch `phase-1/tenancy-auth`. Tenant isolation is built and
proven at all three layers; Identity, JWT, RBAC and PIN login are in. 133 tests green, the Data and Api
suites against real Postgres via Testcontainers.

Pick up at [`docs/ROADMAP.md`](docs/ROADMAP.md) → Phase 2, and read
[`docs/HANDOFF.md`](docs/HANDOFF.md) first for session state.
