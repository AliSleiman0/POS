# POS — Roadmap

**This is the "where are we?" file.** Every phase and milestone below has exit criteria as a checkbox. Update it as work lands. Read `DECISIONS.md` first for *why*, then this file for *what next*, then `docs/phases/PHASE-N-*.md` for *how*.

- Architecture and layering rules → [ARCHITECTURE.md](ARCHITECTURE.md)
- Entities, money rules, indexes → [DATA-MODEL.md](DATA-MODEL.md)
- Endpoint contracts → [API.md](API.md)
- Working conventions for a coding session → [../CLAUDE.md](../CLAUDE.md)

## Status

| | |
|---|---|
| **Current phase** | Phase 1 complete — 1.1–1.7 done, 133 tests green |
| **Next up** | Phase 2, catalog & inventory. Phase 1's gate is passed and merged to `main`. |
| **MVP definition** | Phases 0–8 complete = shippable retail POS |
| **Last updated** | 2026-07-31 |

## Phase overview

| Phase | Name | Scope | Status |
|---|---|---|---|
| 0 | [Foundation & environment](phases/PHASE-0-foundation.md) | Tooling, solution scaffold, docs, CI | ✅ Done |
| 1 | [Multi-tenancy & auth spine](phases/PHASE-1-tenancy-auth.md) | Tenant isolation, Identity, JWT, RBAC, PIN login | ✅ Done |
| 2 | [Catalog & inventory](phases/PHASE-2-catalog-inventory.md) | Products, barcodes, categories, stock ledger | ⬜ Not started |
| 3 | [Checkout & sales (cash)](phases/PHASE-3-checkout-sales.md) | Money, pricing engine, tender, idempotency, shifts | ⬜ Not started |
| 4 | [Web: shell, auth, catalog](phases/PHASE-4-web-shell-catalog.md) | SPA shell, login, product management UI | ⬜ Not started |
| 5 | [Web: register screen](phases/PHASE-5-web-register.md) | Scan → cart → cash tender → sale | ⬜ Not started |
| 6 | [Receipts & reporting](phases/PHASE-6-receipts-reporting.md) | Receipt render/print, Z-report, sale history | ⬜ Not started |
| 7 | [Employees, roles & audit](phases/PHASE-7-employees-audit.md) | Employee CRUD UI, audit log | ⬜ Not started |
| 8 | [Deployment & hardening](phases/PHASE-8-deployment.md) | Containerize, host, backups, security | ⬜ Not started |
| — | **← MVP line.** Everything above ships as v1. | | |
| 9 | [Offline (PWA)](phases/PHASE-9-offline.md) | Service worker, local catalog, outbox, reconciliation | ⬜ Not started |
| 10+ | [Beyond MVP](#beyond-mvp) | Restaurant mode, card payments, desktop, platform admin | ⬜ Not started |

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

- [ ] **2.1 Entities** — `Product`, `Barcode` (many per product), `Category`, `StockItem`, `TaxClass`.
- [ ] **2.2 CRUD API** — cursor pagination, search by name/SKU, soft-delete via `IsActive` only (historical sale lines reference products forever).
- [ ] **2.3 Barcode lookup** — `GET /products/by-barcode/{code}`, unique index on `(TenantId, Code)`. Hottest path in the app.
- [ ] **2.4 Stock movement ledger** — `Receive`/`Adjust`/`Sale`/`Refund`/`Waste` with reason + actor; on-hand is a cached projection of the ledger, never a bare mutable number.
- [ ] **2.5 Tests** — Core unit tests + API integration tests including tenant isolation.

## Phase 3 — Checkout & sales (cash)

The money phase. All rules live in `Pos.Core` as pure, DB-free logic. Detail: [phases/PHASE-3-checkout-sales.md](phases/PHASE-3-checkout-sales.md)

- [ ] **3.1 Money type** — `decimal` over `numeric(19,4)`, `MidpointRounding.AwayFromZero`, rounded once at the total. Never `float`/`double`.
- [ ] **3.2 Pricing engine** — deterministic pipeline: line subtotal → line discount → cart discount → tax → total. Per-tenant `TaxMode` (`Inclusive`/`Exclusive`) decided here because it is not retrofittable.
- [ ] **3.3 Price snapshotting** — each `SaleLine` stores unit price, tax rate and discount as of sale time; reports never join to current `Product.Price`.
- [ ] **3.4 Cash tender** — multiple tender lines, change due, over/under-tender rules, `Method` discriminator so card is additive later.
- [ ] **3.5 Idempotent submit** — client-generated `ClientTransactionId` (GUID) unique per tenant; a replay returns the original sale. The primitive Phase 9 depends on — built now, not bolted on.
- [ ] **3.6 Atomic commit** — sale + stock movements + sale number in one transaction; optimistic concurrency on `StockItem`; oversell allowed but **flagged for review**, never silently corrected.
- [ ] **3.7 Append-only ledger** — completed sales are never mutated or deleted; voids and refunds are new linked rows.
- [ ] **3.8 Register shifts** — open with float → cash movements → close with counted cash and computed variance.
- [ ] **3.9 Tests** — exhaustive pricing/rounding/tender unit tests; integration tests for replay, concurrent last-unit sale, refund.

## Phase 4 — Web: shell, auth, catalog

Detail: [phases/PHASE-4-web-shell-catalog.md](phases/PHASE-4-web-shell-catalog.md)

- [ ] **4.1 App shell** — routing, layout, error boundary, TanStack Query, API client **generated from OpenAPI** so DTOs can't drift from the backend.
- [ ] **4.2 Auth flow** — login, silent refresh, role-aware route guards, cashier PIN swap, session expiry.
- [ ] **4.3 Catalog UI** — product list/search/create/edit, categories, stock adjustment.
- [ ] **4.4 Tests** — Vitest on components/hooks; Playwright: login → create product → see it listed.

## Phase 5 — Web: register screen

The screen that decides whether the product is usable. Detail: [phases/PHASE-5-web-register.md](phases/PHASE-5-web-register.md)

- [ ] **5.1 Register layout** — cart pane + keypad + product grid; dense, touch-first, fully keyboard-operable.
- [ ] **5.2 Scan input** — global keystroke capture that works with no field focused and does not fight manual entry.
- [ ] **5.3 Cart interactions** — qty, line discount, line void, cart void, price override (permission-gated).
- [ ] **5.4 Cash payment** — tender screen, quick-cash buttons, change due legible across a counter.
- [ ] **5.5 Double-submit safety** — reuses the 3.5 idempotency key; a disabled button is not the mechanism.
- [ ] **5.6 Tests** — Playwright: scan → cart → tender → sale recorded → stock decremented.

## Phase 6 — Receipts & reporting

Detail: [phases/PHASE-6-receipts-reporting.md](phases/PHASE-6-receipts-reporting.md)

- [ ] **6.1 Receipt model** — one server-rendered payload feeding browser print, future thermal printer and email.
- [ ] **6.2 Browser printing** — 80mm print stylesheet; reprint from history.
- [ ] **6.3 Z-report** — per shift and per business day: gross, discounts, tax, net, tender breakdown, cash variance, voids/refunds.
- [ ] **6.4 Sale history** — search, filter, detail view, permission-gated refund initiation.

## Phase 7 — Employees, roles & audit

Detail: [phases/PHASE-7-employees-audit.md](phases/PHASE-7-employees-audit.md)

- [ ] **7.1 Employee management UI** — create/invite, assign role, set/reset PIN, deactivate. Owner/Manager only.
- [ ] **7.2 Audit log** — append-only: price override, discount, void, refund, stock adjust, role change; actor + timestamp + before/after.
- [ ] **7.3 Authorization tests** — a Cashier is proven *rejected* on every Manager/Owner endpoint. Negative paths, not just happy ones.

## Phase 8 — Deployment & hardening

Detail: [phases/PHASE-8-deployment.md](phases/PHASE-8-deployment.md)

- [ ] **8.1 Containerize** — multi-stage `Dockerfile` for the API; web built to static assets.
- [ ] **8.2 Hosting** — API container + managed Postgres; web on static host/CDN; HTTPS; CORS locked to the web origin.
- [ ] **8.3 Migrations in CI/CD** — explicit deploy step. Not `EnsureCreated()`, not migrate-on-startup (races with >1 instance).
- [ ] **8.4 Observability** — structured logs with `TenantId` on every scope, health checks, error tracking.
- [ ] **8.5 Backups + restore drill** — automated backups **and an actually-executed restore test**. An untested backup is not a backup.
- [ ] **8.6 Security pass** — rate limiting, security headers, dependency audit, secrets from env/vault, cross-tenant probe against the deployed instance.
- [ ] **8.7 Tenant onboarding** — repeatable script to create tenant + Owner. No platform admin UI yet (per `DECISIONS.md`); direct DB inspection is the accepted stopgap.

## Phase 9 — Offline (PWA)

Deliberately post-MVP per `DECISIONS.md`. Rests on 3.5. Detail: [phases/PHASE-9-offline.md](phases/PHASE-9-offline.md)

- [ ] **9.1 Service worker + app-shell cache** — app loads with no network.
- [ ] **9.2 Local catalog mirror** — products/barcodes/prices in IndexedDB; scanning and cart-building work offline.
- [ ] **9.3 Outbox queue** — sales queued with their client GUID, replayed on reconnect, with sync state visible in the UI.
- [ ] **9.4 Conflict reconciliation** — oversell at sync is flagged for staff review, never silently corrected.
- [ ] **9.5 Honest limits** — browser storage is evictable; web offline is documented and surfaced as best-effort.

## Beyond MVP

Scoped, not yet planned in detail. Order is a guess; revisit after the first paying client.

| Phase | Name | Notes |
|---|---|---|
| 10 | Restaurant mode | Tables/tabs, modifiers, split bills, kitchen routing. A genuinely different order model — **not** a bolt-on to `Sale`, per `DECISIONS.md`. |
| 11 | Card payments | `IPaymentProvider` port + Stripe adapter; Stripe Terminal for physical readers. |
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
