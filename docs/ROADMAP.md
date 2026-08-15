# POS — Roadmap

**This is the "where are we?" file.** Every phase and milestone below has exit criteria as a checkbox. Update it as work lands. Read `DECISIONS.md` first for *why*, then this file for *what next*, then `docs/phases/PHASE-N-*.md` for *how*.

- Architecture and layering rules → [ARCHITECTURE.md](ARCHITECTURE.md)
- Entities, money rules, indexes → [DATA-MODEL.md](DATA-MODEL.md)
- Endpoint contracts → [API.md](API.md)
- Working conventions for a coding session → [../CLAUDE.md](../CLAUDE.md)

## Status

| | |
|---|---|
| **Current phase** | **Phase 9 built and green**, with four gaps recorded rather than hidden — see the phase doc's *What is not done*. A till sells with the network off and the sale lands exactly once, dated when the customer paid. 1441 .NET + 321 Vitest + 70 Playwright green |
| **Next up** | **A real shop.** `DECISIONS.md` has argued for this since Phase 8 and the argument is stronger now: nobody who has worked a till has used any of it, and that is still the only untested claim that matters. The four Phase 9 gaps are the first thing to finish if a shop asks for them. |
| **MVP definition** | Phases 0–8 complete = shippable retail POS. **Phase 8 is the last one before the line.** |
| **Last updated** | 2026-08-11 |

## Phase overview

| Phase | Name | Scope | Status |
|---|---|---|---|
| 0 | [Foundation & environment](phases/PHASE-0-foundation.md) | Tooling, solution scaffold, docs, CI | ✅ Done |
| 1 | [Multi-tenancy & auth spine](phases/PHASE-1-tenancy-auth.md) | Tenant isolation, Identity, JWT, RBAC, PIN login | ✅ Done |
| 2 | [Catalog & inventory](phases/PHASE-2-catalog-inventory.md) | Products, barcodes, categories, stock ledger | ✅ Done |
| 3 | [Checkout & sales (cash)](phases/PHASE-3-checkout-sales.md) | Money, pricing engine, tender, idempotency, shifts | ✅ Done |
| 4 | [Web: shell, auth, catalog](phases/PHASE-4-web-shell-catalog.md) | SPA shell, login, product management UI | ✅ Done |
| 5 | [Web: register screen](phases/PHASE-5-web-register.md) | Scan → cart → cash tender → sale | ✅ Done |
| 6 | [Receipts & reporting](phases/PHASE-6-receipts-reporting.md) | Receipt render/print, Z-report, sale history | ✅ Done |
| 7 | [Employees, roles & audit](phases/PHASE-7-employees-audit.md) | Employee CRUD UI, audit log, settings | ✅ Done |
| 8 | [Deployment & hardening](phases/PHASE-8-deployment.md) | Containerize, host, backups, security | ✅ Done |
| — | **← MVP line.** Everything above ships as v1. | | |
| 9 | [Offline (PWA)](phases/PHASE-9-offline.md) | Service worker, local catalog, outbox, reconciliation | ✅ Done (4 gaps recorded) |
| 10 | [Restaurant mode](phases/PHASE-10-restaurant.md) | Tables, orders, modifiers, kitchen display, split bills, tips | 🔨 In progress |
| 11+ | [Beyond MVP](#beyond-mvp) | Desktop, platform admin (card payments dropped) | ⬜ Not started |

Legend: ⬜ not started · 🔨 in progress · ✅ done · ⏸️ blocked

---

## Phase 0 — Foundation & environment

Detail: [phases/PHASE-0-foundation.md](phases/PHASE-0-foundation.md)

- [x] **0.1 Prerequisites** — .NET SDK 10.0.302, `dotnet-ef` 10.0.10, WSL 2.7.11, Docker Desktop 4.84.0 (engine 29.6.2, Compose v5.3.1). Verified 2026-07-30: `docker run hello-world` succeeds.
- [x] **0.2 Solution scaffold** — `Pos.slnx` (.NET 10's new format, not `.sln`), 3 src + 3 test projects, `dotnet build` clean with zero warnings, `Directory.Build.props` + `Directory.Packages.props`. `Pos.Web` arrives in 0.5.
- [x] **0.3 Dependency rules enforced** — `Pos.Core` has zero non-BCL references; two architecture tests (csproj declaration + compiled assembly references) with the failure mode verified by deliberately breaking it.
- [x] **0.4 Local Postgres** — Postgres 17.10 + pgAdmin via Compose; `AppDbContext` connects with model-wide conventions (`numeric(19,4)`, `timestamptz`, snake_case); `Initial` migration applied; secrets in user-secrets only. `/health/ready` verified to actually fail with the DB stopped.
- [x] **0.5 Web scaffold** — Vite 8 + React 19 + TS 6 + Tailwind v4 + shadcn/ui, oxlint (template default) + Prettier, Vitest (11 tests) + Playwright wired. `strict` was absent from the template and had to be added. Dev proxy verified reaching the API.
- [x] **0.6 Docs** — `docs/` set + `CLAUDE.md` written, `DECISIONS.md` updated.
- [x] **0.7 CI** — GitHub Actions builds and tests both stacks on push and PR; `.editorconfig`, `.gitignore`, `.gitattributes` (landed early in 0.2). Negative verification performed: a deliberate warning and type error failed both jobs for the intended reasons.

## Phase 1 — Multi-tenancy & auth spine

The load-bearing phase. Everything after it assumes tenant scoping is automatic and unforgeable. Detail: [phases/PHASE-1-tenancy-auth.md](phases/PHASE-1-tenancy-auth.md)

- [x] **1.1 Tenant primitives** — `ITenantContext` resolved per request from the JWT `tenant_id` claim; `Tenant` entity; abstract `TenantEntity` base. Unresolved tenant **throws**; `AmbientTenantContext` resolves once per scope and refuses to switch.
- [x] **1.2 Automatic scoping** — query filters applied by reflection to every `ITenantOwned` (an interface, not the base class — `ApplicationUser` must inherit `IdentityUser<Guid>`); `SaveChangesInterceptor` stamps on insert and **rejects** an insert carrying another tenant's id rather than correcting it. Proved against a throwaway entity that nothing registers.
- [x] **1.3 Tenant-scoped Identity** — `ApplicationUser : ITenantOwned`; Identity's platform-wide unique indexes replaced with composites leading on `TenantId`; Identity's satellite tables carry `TenantId`; the .NET 10 passkey table is ignored outright.
- [x] **1.4 JWT + RBAC** — access + rotating refresh tokens with `FamilyId` revoke-on-reuse; named policies registered from one catalog that a test compares against the table in `ARCHITECTURE.md`. No `FallbackPolicy`: a missing authorization decision fails the build instead of defaulting.
- [x] **1.5 PIN login** — enrolled register + hashed 4–6 digit cashier PIN. The device check is an authentication *scheme*, so an unenrolled till is turned away before the PIN is read. Two independent caps: per-user lockout, and a rate limit partitioned on the device token so one till cannot walk the staff list to get around it.
- [x] **1.6 RLS hardening** — Postgres row-level security on `current_setting('app.tenant_id')`, published session-level by a connection interceptor. The app connects as `pos_app` (`NOBYPASSRLS`); migrations run as the owner. Enabled, forced and policied by a catalog loop, not a hand-written table list — **but an applied migration does not re-run, so a Phase 2 migration adding a tenant table must call `ApplyTenantRowLevelSecurity()`**; a test fails the build if it does not.
- [x] **1.7 Isolation test suite** — two tenants seeded with identical data, so a leak doubles a list instead of having to be spotted by id. Every read path (endpoint, `DbContext`, raw SQL) proven blind to the other tenant; a cross-tenant `TenantId` in a request body is never honoured. Driven off a **manifest of every endpoint** that a test diffs against the router's own table — **so Phase 2 adds a row, not a test file, and forgetting fails the build**.

## Phase 2 — Catalog & inventory

Detail: [phases/PHASE-2-catalog-inventory.md](phases/PHASE-2-catalog-inventory.md)

- [x] **2.1 Entities** — `Product`, `Barcode` (many per product), `Category`, `StockItem`, `TaxClass`. Foreign keys carry `tenant_id` and point at `(tenant_id, id)` alternate keys, because **referential integrity checks bypass RLS** — a single-column key would accept a cross-tenant reference. `StockItem.RowVersion` maps to `xmin`. 178 tests green.
- [x] **2.2 CRUD API** — products, categories and tax classes behind one cursor-paged envelope. `costPrice` **omitted** (not nulled) for callers without `CanViewMargins`, via two projections so a Cashier's SQL never names the column. `?q=` is a case-insensitive contains served by a `pg_trgm` GIN index, with the term escaped so `%` is literal. Duplicate SKU is a caught `23505`, never a pre-check. `activate` routes added — `deactivate` alone made a mis-click permanent. 414 tests green.
- [x] **2.3 Barcode lookup** — `GET /products/by-barcode/{code}` answers with the product, its price and its tax rate in **one statement** (asserted on the generated SQL). A deactivated product still scans, carrying `isActive: false`. `GET`/`POST /products/{id}/barcodes` and `DELETE .../{barcodeId}`; `isPrimary` stays advisory by decision. No migration — 2.1 had already built the table and the index. 404 tests green.
- [x] **2.4 Stock movement ledger** — `StockMovement` append-only and signed, written only through `IStockLedger` (the first port Core declares) so the movement and `StockItem.OnHand` move in one transaction or not at all. `reason` required, direction checked against the type, `Sale`/`Refund` refused by hand. `GET /stock`, `GET /stock/{productId}/movements`, `POST /stock/adjustments`. **Not idempotent — deferred to 3.5 and recorded.** 569 tests green.
- [x] **2.5 Tests** — every new endpoint has an isolation manifest row and a negative-authorization row; the ledger invariant is asserted over a randomised sequence; a six-break falsification pass went red six times out of six.

## Phase 3 — Checkout & sales (cash)

The money phase. All rules live in `Pos.Core` as pure, DB-free logic. Detail: [phases/PHASE-3-checkout-sales.md](phases/PHASE-3-checkout-sales.md)

- [x] **3.1 Money type** — `Money` as a `readonly record struct` over `decimal`, EF-mapped model-wide so entity amounts are `Money` while API DTOs stay `decimal`. No `operator +(Money, decimal)`, so raw decimal arithmetic on a price does not compile. `Tenant.CashRoundingIncrement` added; `CatalogRules` kept but now shares one `Rounding` primitive, and its tests passed unedited. `ArchitectureTests`' clock check became a real Mono.Cecil IL scan.
- [x] **3.2 Pricing engine** — pure pipeline, both tax modes, cart discount apportioned with the remainder to the largest line. `Subtotal` is **derived** so invariant 1 holds exactly on stored two-decimal values. A hand-computed inclusive mixed-rate discounted cart is pinned by `HandCheckedCartTests`. Property tests over 500 seeded random carts.
- [x] **3.3 Entities + price snapshotting** — `Sale`, `SaleLine`, `Tender`, `Shift`, `CashMovement`, `StockDiscrepancy`, `SaleSequence`. `Sale.TaxMode` and `SaleLine.OriginalSaleLineId` added beyond DATA-MODEL. `stock_movement.sale_id` finally got its composite FK.
- [x] **3.4 Cash tender** — multiple tenders, change due, under-tender as `409`, `Cash` the only accepted method. `Adding_a_method_needs_no_sale_schema_change` turns the discriminator claim into an assertion.
- [x] **3.5 Idempotent submit** — `RequestFingerprint` over raw body bytes, `IdempotencyRecord`, an endpoint filter plus a writer callback so the key lands in the same transaction as the work. Retrofitted onto `POST /stock/adjustments`, breaking the test 2.4 left for it.
- [x] **3.6 Atomic commit** — `ISaleWriter`, sale-number counter upserted in-transaction, shift `FOR SHARE`, `StockDiscrepancy` on a negative on-hand, `GET /stock/discrepancies`. Shift open/current landed here because sales depend on them.
- [x] **3.7 Append-only ledger** — void as a status flag plus compensating movements; refund as a new linked `Refund` sale re-priced from snapshots with a proportional discount share. `No_route_updates_or_deletes_a_sale` enumerates the routing table.
- [x] **3.8 Register shifts** — open/close lifecycle, cash movements with a sign rule, expected cash and stored variance. The close's exclusive lock and the sale's share lock make a concurrent sale deterministic.
- [x] **3.9 Tests** — `SalesFixture`, `TradingTenant`, 9 new isolation manifest rows, `No_endpoint_dto_declares_a_money_property`, and the randomised sale/void/refund ledger property. Falsification: 15 deliberate breaks across the phase, and the two that caught nothing were closed with new tests.

## Phase 4 — Web: shell, auth, catalog

Detail: [phases/PHASE-4-web-shell-catalog.md](phases/PHASE-4-web-shell-catalog.md)

- [x] **4.1 App shell** — router, authenticated layout, error boundary, in-page toasts, TanStack Query with two staleness tiers (catalog 60s; anything drawer- or money-adjacent always refetched). `openapi-typescript` + `openapi-fetch`, output committed with a CI drift check. Generating the client found two contract gaps that were invisible from the backend: all five `/auth` handlers returned bare `IResult` so their bodies typed as `never`, and `Idempotency-Key` was enforced but undocumented so no generated client could send it. Both fixed and pinned.
- [x] **4.2 Auth flow** — access token in memory, refresh token in `sessionStorage` (trade-off in `DECISIONS.md`; the httpOnly-cookie question is deferred to 8.2, which decides the topology). **One** refresh shared by concurrent 401s, because rotation plus revoke-on-reuse turns five parallel refreshes into a logout. An expired session renders a prompt **over** the current route rather than navigating, so Phase 5's cart survives. Device enrolment and PIN swap.
- [x] **4.3 Catalog UI** — product list on cursor pagination, create/edit mirroring the server's validation, scan-to-add barcodes, categories, tax classes, stock adjustment with a required reason and an idempotency key minted per dialog rather than per attempt. Loading, empty and error states on every list.
- [x] **4.4 Tests** — 66 Vitest (single-flight refresh, `problem+json` mapping, money, guards, validation) and 11 Playwright specs against a **real API and a real Postgres** in a dedicated `pos_e2e` database. CI gained a contract-drift job and an e2e job. The suite found two real bugs — see below.

## Phase 5 — Web: register screen

The screen that decides whether the product is usable. Detail: [phases/PHASE-5-web-register.md](phases/PHASE-5-web-register.md)

- [x] **5.1 Register layout** — cart pane + total/keypad + product grid at `/register`, inside `AppLayout` so the cart (held in a `CartProvider` above the router's outlet) survives a route change and a re-auth. Open-shift prompt on the screen rather than in a menu. `POST /sales/quote` pulled forward from 5.3 so the headline number is the server's, with an integer minor-unit subtotal marked provisional until it lands.
- [x] **5.2 Scan input** — global keystroke capture that stands down for focused fields, told from human typing by a **budget over the whole burst** rather than a per-gap threshold: one stalled frame used to split a code and look up its tail. Double-fire suppressed within 300ms of the previous scan starting. Unknown code is an in-page banner; feedback is a WebAudio beep plus a line flash, mutable.
- [x] **5.3 Cart interactions** — line and cart discount, price override, and a **manager override**: `POST /auth/override` exchanges a manager's PIN at the enrolled till for a single-use grant that `POST /sales` consumes inside the sale's transaction. The cashier's session is untouched, so the sale stays theirs while `SaleLine.OverriddenBy` names the manager. The quote enforces the same policies without spending the grant, so the till can never display a total the sale would refuse.
- [x] **5.4 Cash payment** — the tender pad replaces the total in the right-hand column so the cart stays visible; quick cash, split tender with a running balance, and change due at `text-6xl` from the server's `changeGiven`. **The idempotency key lifecycle came forward from 5.5**: minted when tendering begins, reused on every attempt, and a replay is shown to the cashier — pinned by an e2e test that lets the first request commit and drops its response. A receipt action is deferred to 6.1, which is where the payload it would print gets built.
- [x] **5.5 Double-submit safety** — the cart is persisted to `sessionStorage` on every change, so a reload keeps the basket *and* the sale's GUID. A reload mid-payment is resolved by **asking** the server what the key bought (`GET /sales/by-client-transaction/{id}`, new), never by re-POSTing to find out — re-submitting takes the money in exactly the case that must not be charged. Three answers, not two: taken, not taken, and *cannot be determined*, which gets a banner telling the cashier not to re-ring it. Also closes a 5.4 dead end: the same key with an edited basket is `409 idempotency-key-reused`, and the till now names the sale that was already paid for instead of saying "try again" for ever.
- [x] **5.6 Tests** — Vitest and Playwright, green locally and in CI. "Reprint the receipt" is the one clause not met: `GET /sales/{id}/receipt` is unbuilt, so it is deferred to 6.1 along with the rest of the receipt work.

## Phase 6 — Receipts & reporting

Detail: [phases/PHASE-6-receipts-reporting.md](phases/PHASE-6-receipts-reporting.md)

- [x] **6.1 Receipt model** — one server-rendered payload feeding browser print, future thermal printer and email. Tax broken down by rate with the residue placed deliberately, so the parts sum to `TaxTotal` exactly; timestamps in the tenant's zone. **Found that `InvariantGlobalization` had been `true` since Phase 0**, which made an IANA zone unresolvable on Windows and would have split behaviour between the runner and every developer machine — invariant 8 had depended on ICU since it was written. See `DECISIONS.md`.
- [x] **6.2 Browser printing** — 80mm stylesheet with an A4 fallback, and the receipt action Phase 5 left owing on the completion panel. The preview *is* the printed element. Reprints are marked; the mark is client-decided and the limitation is recorded. Reprint *from history* arrives with 6.4, which is where history exists.
- [x] **6.3 Z-report** — per shift and per business day, one shape and one set of aggregations so the two cannot disagree. `BusinessDay` is pure and takes the zone as a parameter; DST is handled at both transitions rather than throwing on the last Sunday in March. A closed shift's variance is **read**, never recomputed; an open one is computed live and labelled provisional. Screens for both, plus Owner-only margins. Found and fixed two real defects: shifts scoped by open date rather than overlap (an overnight drawer read as reconciled), and `GET /stock` silently ignoring the `?q=` the web client has sent since 4.3.
- [x] **6.4 Sale history** — `/sales` list and detail, filters that compose, trading-day date bounds, and search by the number on the customer's receipt. Refund linkage **both ways**, so an original shows what has already been given back. Permission-gated refund initiation with the idempotency key minted per dialog. The history pages newest first; `CursorPaging` gained a per-endpoint direction rather than a global one.

## Phase 7 — Employees, roles & audit

Detail: [phases/PHASE-7-employees-audit.md](phases/PHASE-7-employees-audit.md)

- [x] **7.0 Audit seam** — `AuditEntry` + `IAuditLog`, entries *staged* on the shared scoped `DbContext` so each commits inside whatever transaction its action already runs. `UPDATE`/`DELETE` revoked from `pos_app`, which the Phase 1.6 default privileges had silently granted — falsified, and the `UPDATE` genuinely succeeded without the revoke. `ICurrentActor` gained `RegisterId`. Reordered ahead of 7.1 because six of the fifteen actions belong to 7.1's endpoints.
- [x] **7.1 Employee management UI** — create (initial password, no email invite), assign role, set/reset PIN, deactivate. **Owner only**, not Owner/Manager: whoever sets PINs can create a user who sells. Lock-out guards are three 409s with the owner count taken under `FOR UPDATE`; dropping the lock made the concurrent race fail four runs out of four. `/admin/tills` is the first UI for `POST /registers` and revoke.
- [x] **7.2 Audit log** — fifteen actions, each transactional with its own work. `GET /audit` filtered by action, actor and *trading-day* range. `AuditManifest` fails the build when an enum member has no covering test, so this keeps holding after the phase. Closes the Phase 6.2 reprint debt: `isReprint` is now derived server-side from `ReceiptIssued` entries a client cannot suppress.
- [x] **7.3 Authorization tests** — a (role × endpoint) matrix **derived** from `PolicyCatalog` × the routing table, so tomorrow's endpoints are covered too. 229 cases, no writes. The limit is recorded rather than assumed: it cannot catch an endpoint declaring the *wrong* policy, which the manifest's `Refused` lists and hand-written tests do.
- [x] **7.4 Settings** — `GET`/`PUT /settings`. Added because 7.2 audits `SettingsChanged` and there was no route to audit; it also closes a real MVP gap, since receipt fields and cash rounding were reachable only through the unshipped seeder. `taxMode` refused once sales exist.

## Phase 8 — Deployment & hardening

Detail: [phases/PHASE-8-deployment.md](phases/PHASE-8-deployment.md)

- [x] **8.1 Containerize** — `aspnet:10.0-noble-chiseled-extra` (`-extra` is load-bearing: plain chiseled ships no ICU and no tzdata, so every IANA zone breaks *inside the container only*). 168 MB, non-root, no SDK/source/shell, secrets inspected rather than assumed. `Hosting:BehindTlsTerminatingProxy` prevents the redirect loop an edge proxy would otherwise cause. The web client stopped assuming same-origin — including two raw `fetch` calls that bypassed the typed client, one of which would have signed every cashier out at the first token rotation.
- [x] **8.2 Hosting** — **deployed and verified.** **Render**, not Fly: Fly requires a payment card. CORS locked to exact origins with an empty Production list refusing to boot; RLS role verified by the readiness probe; `render.yaml` defines all three resources; connection strings accepted in URI *or* keyword form. Live at `pos-api-jc43` / `pos-web-lcc5`, `pos_app` created NOBYPASSRLS, 15 migrations applied, `/health/ready` green, CORS verified both ways, OpenAPI/Scalar 404. Two free-plan limitations accepted and recorded: the API sleeps after ~15 min idle, and the database is deleted after 30 days.
- [x] **8.3 Migrations in CI/CD** — `deploy.yml` gated on CI, idempotent SQL applied as the owner through a proxied `psql`, image deployed by digest so a rollback is a redeploy. `StartupMigrationTests` IL-scans the three shipped assemblies and was falsified. Dependency audits added to CI. Script verified building a schema from nothing. **The pipeline has not run for real.**
- [x] **8.4 Observability** — JSON logs with `TenantId`/`UserId`/`RegisterId` on every request scope; Sentry behind a scrubber tested as a security boundary; Owner-only `POST /diagnostics/test-error`; four metrics. Two real bugs in the metrics filter were caught by its own tests.
- [x] **8.5 Backups + restore drill** — **executed 2026-08-11**, and it found a real defect: `FORCE ROW LEVEL SECURITY` makes `pg_dump` exit 1 while still leaving a plausible 89 KB file with the users table missing. Corrected procedure, timings and verification queries in `RUNBOOK.md`. **Free Postgres has no automatic backups and self-deletes after 30 days** — the dump is the backup.
- [x] **8.6 Security pass** — **done, including the production probe.** Login rate limit (set for a shop behind NAT, not a person), security headers, CSP verified in a browser against the built app, dependency audit in CI. `tools/Pos.Probe` replays the exported isolation manifest over HTTPS against the deployed instance: 33 claims, no violations, by-id routes answering 404 rather than 403. Falsified by pointing it at itself (11 violations, exit 1).
- [x] **8.7 Tenant onboarding** — `Pos.Seed onboard`: requires an explicit connection, requires `TaxMode` and the business-day offset, generates a password shown once, refuses an existing slug. Verified by onboarding a shop and logging into it. Runbook covers onboarding, a locked-out Owner, a lost device, a disputed total, and what to do when each kind of credential leaks.

## Phase 9 — Offline (PWA)

Deliberately post-MVP per `DECISIONS.md`. Rests on 3.5. Detail: [phases/PHASE-9-offline.md](phases/PHASE-9-offline.md)

**Three pieces of server groundwork came first**, because the phase is unbuildable without them and reading the code is what exposed that:

- [x] **9.0a Offline sale timestamps** — `POST /sales` accepts `occurredAt`; `Sale` gains a server-set `RecordedAt`. Without it a sale rung at 22:00 and replayed at 09:00 lands in the wrong trading day, in the wrong Z-report, against a drawer already counted. Bounded by `OfflineSaleRules`; outside the bounds is a permanent `422` so an outbox sends it to a person rather than retrying.
- [x] **9.0b Pricing conformance corpus + TypeScript port** — the phase doc assumed an offline cart could be tendered; it could not, because every total came from `POST /sales/quote`. `Pos.Core.Pricing` is ported to TS over a faithful `decimal`, and both engines are asserted against one committed 913-cart corpus. **An explicit amendment to invariant 3**, in `DECISIONS.md`. Falsified six ways.
- [x] **9.0c `GET /catalog/sync` + barcode soft delete** — `GET /products` had no changed-since filter, barcodes were readable one product at a time, and a hard-deleted barcode is invisible to any incremental feed. Two independent cursors; the feed may repeat a row and cannot skip one.

- [x] **9.1 Service worker + app-shell cache** — app loads with no network; `registerType: 'prompt'` and a second gate so an update never applies mid-sale. Asserted against the emitted `sw.js`, not the config.
- [x] **9.2 Local catalog mirror** — IndexedDB per tenant; mirror-first scanning online as well as off; incremental sync with the watermark committed only after a full walk; mirror age on screen. *Price-change-on-reconnect and a measured 10k catalog are not done.*
- [x] **9.3 Outbox queue** — written before any network attempt, replayed as ordinary `POST /sales` with the original key, serial and classified. Proved end to end: a sale taken offline lands exactly once, dated when the customer paid. *Offline shift close is not done.*
- [x] **9.4 Conflict reconciliation** — `/sync` explains each refusal, its consequence for the money, and the one action. A reused idempotency key deliberately offers **no** retry. Re-file carries the original `occurredAt` and a new key.
- [x] **9.5 Honest limits** — persistence requested and its *answer* surfaced; warnings on refused storage, a long offline window and a large queue; a test rejects the words safe, secure and guaranteed.

**Four gaps are recorded in the phase doc rather than hidden:** offline shift close, price-change-on-reconnect, a measured 10k-product sync, and the oversell case proved through two offline browsers. **Phase 10 does not close any of them** — they stay open and are still the phase doc's own list.

## Phase 10 — Restaurant mode

`DECISIONS.md`'s V2. Detail: [phases/PHASE-10-restaurant.md](phases/PHASE-10-restaurant.md)

**The shape, decided before any of it was built:** an order is mutable pre-checkout working state and a `Sale` stays the append-only financial record, so paying a bill is an ordinary commit through `ISaleWriter` — one pricing engine, one idempotency contract, one stock ledger, one Z-report. `OrderBill.SaleId` points from the restaurant model *to* `Sale`, never back, so `Sale` never learns what an order is. That is how "a genuinely different order model, **not** a bolt-on to `Sale`" and "no second money path" hold at the same time.

- [x] **10.0 Service mode** — `Tenant.ServiceMode` (`Retail`|`Restaurant`), defaulted in the column so every pre-existing shop is a counter. Editable at `/admin/settings` and audited per changed key, unlike `TaxMode`, because a service mode decides which screens a shop sees rather than what a stored price means. Mirrored through `GET /catalog/sync` so an offline till knows which product it is. **Required on the request, not defaulted** — an omitted enum binds to `Retail`, so an older client would have posted a complete-looking body that turned a restaurant back into a counter; found by the existing tests passing when they should not have, and now its own test.
- [x] **10.1 Order model** — `ServiceArea`, `DiningTable`, `Order`, `OrderLine`, `OrderSequence`; `OrderWriter` in `Pos.Data`, a public class rather than a Core port on `ShiftWriter`'s reasoning. One open order per table enforced by a **filtered unique index** and proved with two writers racing; line numbers allocated under a `FOR UPDATE` from `MAX`, so a void never hands its number on. Tables are `customer_order` and `dining_table` because `ORDER` and `TABLE` are reserved words and this codebase writes raw SQL in the money paths.
- [x] **10.2 Orders API** — open, add, amend, void, transfer, merge, abandon, plus the floor itself. Fourteen isolation-manifest rows. Order routes on a retail tenant answer `409 restaurant-mode-required` — a state conflict, not an authorization failure, and the route stays mapped so the coverage tests still see it. **Found a real leak in shipped Phase 9 code**: `GET /catalog/sync` read its settings block with no `Where`, so every till mirrored an arbitrary shop's currency, tax mode and rounding — and offline, `taxMode` prices the cart. It passed its test because the isolation world seeds both tenants identically; the first field that differed was `serviceMode`.
- [x] **10.3 Modifiers** — a modifier *is* a product (`Product.IsModifier`), so it carries a price, a tax class and stock behaviour and prices through the same engine; a child `OrderLine` with `ParentOrderLineId` nests it on the screen, the ticket and the receipt, and it travels with its parent when voided. `ModifierRules` is pure and enforced at the API, because the sheet's gating is a courtesy. Modifiers are excluded from the product list, the register grid and the offline mirror's search.
- [ ] **10.4 Courses, seats, stations, kitchen display** — **not built.** `Course`, `SeatNumber`, `Fired`/`FiredAt` and the void rules exist and are respected; nothing fires. Self-contained: the columns it needs are already there.
- [x] **10.5 Bills, splitting, payment** — **the keystone, and it holds.** Paying a bill writes an ordinary `Sale` through `ISaleWriter` — same number counter, lines, tender, stock movement, Z-report. `OrderBill.SaleId` is the only link and it points one way, so the retail path is unaware any of this exists. Allocation is refused rather than clamped; a shared line carries its discount proportionally; a bill prices from the order line's snapshots, proved by changing the menu underneath. **An even split is N tenders on one sale**, no new mechanism and no fractional-quantity rounding.
- [x] **10.6 Tips** — `Sale.TipAmount`, and DATA-MODEL invariant 2 amended to `sum(Tender.Amount) >= Total + TipAmount`. The tip comes out of the **change**, not into the total, so `ShiftArithmetic` is untouched and the money lands in expected cash by itself; the Z-report gains a Tips line so a drawer over by exactly the evening's tips reads as that rather than as a surplus.
- [ ] **10.7 Restaurant UI** — **not built.** `/register` still renders the retail screen for every tenant. Every endpoint below works and none has a caller, so **a shop cannot use any of this today**.
- [ ] **10.8 Reporting** — not built, except the Z-report's tips line, which shipped with 10.6 because it decides whether a drawer reconciles.
- [ ] **10.9 Tests** — 244 new .NET tests landed with the milestones above. Missing: the e2e spec and the `Pos.Seed --restaurant` fixture, both of which belong with 10.7 rather than after it.

**Phase 10 is in progress and its gaps are listed in the phase doc rather than hidden.** The server side of an order's life is complete and tested against a real Postgres; the kitchen, every screen, and the e2e spec are not.

**The cost, stated rather than discovered:** a restaurant tenant loses offline trading. An order lives on the server so a second tablet can see the table; Phase 9's outbox queues sales and knows nothing about orders. The app says so in `OfflineLimitsPanel`.

## Beyond MVP

Scoped, not yet planned in detail. Order is a guess; revisit after the first paying client.

| Phase | Name | Notes |
|---|---|---|
| ~~11~~ | ~~Card payments~~ | **Dropped 2026-07-31 — the product takes cash only.** Not deferred: no processor is planned. `Tender.Method` remains a discriminator so a standalone terminal would be additive, but nothing is built for it. See [`DECISIONS.md`](../DECISIONS.md#feature-roadmap-phased). |
| 12 | Avalonia desktop | Same API, durable local DB, real offline. |
| 13 | Business layer | Platform admin, loyalty, purchase orders, low-stock alerts, gift cards, analytics. |

## Definition of done, per phase

A phase is done when:

1. Every milestone's exit criteria is met.
2. `dotnet build` and `pnpm build` are warning-free (warnings are errors).
3. **The phase's own tests are written and green** — locally *and* in CI. See below.
4. The checkboxes here and the phase doc are updated.
5. Work is committed on a branch with a phase-scoped message.

### Test the phase before starting the next one

**Testing is not allowed to accumulate.** Each phase is tested and green before the next phase begins — no "we'll write the tests later", no batched testing pass three phases downstream.

- **UI testing is the one that must never accumulate.** In Phases 4–7 a milestone is not done until its Vitest/Playwright coverage exists *and* somebody has looked at the screen in a browser. jsdom does not paint, so a green component suite is not evidence the UI works — "we'll add Playwright later" is how a frontend ends up with none.
- A trailing `N.x Tests` milestone in a phase doc is the **floor, not the plan**. Tests land with each milestone as it is built (invariant 9 in [`CLAUDE.md`](../CLAUDE.md) — tests ship with the behaviour).
- Green means green **in CI**, not just on this machine. Phase 0 shipped two bugs that were invisible locally and only failed on the runner; `$env:CI="true"` before building reproduces that environment.
- A phase with missing or failing tests is ⏸️ blocked, not ✅ done. If the next phase starts anyway, say so out loud and record it here — don't let it pass silently.

The reason is that the phases are load-bearing on each other. Phase 1's tenant isolation and Phase 3's money and idempotency rules are inherited by everything built after them; a gap found late means auditing all of it by hand instead of reading one failing test.
