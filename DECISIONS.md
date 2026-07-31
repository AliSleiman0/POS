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

**Payments (resolved 2026-07-30): the MVP takes cash only.** This narrows the "cash + card" line above — a deliberate scope cut, not an oversight. Card processing brings processor onboarding, PCI questions and hardware into the phase that most needs to stay small. Shops that already have a standalone card terminal are served by an `External` tender type where staff key in the amount, which reconciles correctly in the Z-report. The tender model is a *collection* of rows with a `Method` discriminator, so adding real card processing later (Phase 11: `IPaymentProvider` port, Stripe first, Stripe Terminal for physical readers) is additive — no `Sale` schema migration.

The mapping from this roadmap to executable phases is [`docs/ROADMAP.md`](docs/ROADMAP.md): MVP = Phases 0–8, offline = Phase 9, restaurant mode = Phase 10.

## Open Questions

### Still unresolved

- **Pricing model conflict**: "sell as a whole, not a license" (one-time) vs. "we host it" (implies ongoing infra cost). Needs a deliberate decision — e.g. one-time fee that includes lifetime hosting, or accept a recurring fee without calling it a "license."
  **Not blocking**: no billing or licensing code exists in the MVP, so this can stay open until Phase 10. It does need deciding before a price is quoted to a real customer, because "lifetime hosting for a one-time fee" is a liability that grows with every tenant.

### Deferred to the phase that decides them

- **Hosting provider** — Fly.io vs. Azure App Service vs. VPS, all viable. Decided and recorded in [Phase 8.2](docs/phases/PHASE-8-deployment.md).
- **Payment processor** — Stripe leads (best API, and Stripe Terminal covers physical readers for the desktop app), but the MVP is cash-only so the choice is deferred to [Phase 11](docs/ROADMAP.md#beyond-mvp) behind an `IPaymentProvider` port.

### Resolved 2026-07-30

- ~~Postgres vs. SQL Server~~ → **Postgres** (see Tech Stack).
- ~~Multi-tenant isolation strategy~~ → **shared DB + `TenantId`**, three enforcement layers (see Hosting & Multi-tenancy).
- ~~Payment processor for MVP~~ → **cash only in the MVP**, card deferred to Phase 11 (see Feature Roadmap).
- Auth approach → **ASP.NET Core Identity + JWT** with two-tier credential/PIN login (see Tech Stack).

## Two decisions that are not retrofittable

Called out because they are cheap now and expensive-to-impossible later:

- **`TaxMode` per tenant** (`Inclusive` for EU-style shelf pricing, `Exclusive` for US-style add-at-till). It determines how every stored price is interpreted, so changing it after trading silently rewrites history. Set at onboarding, refused thereafter. Decided in [Phase 3.2](docs/phases/PHASE-3-checkout-sales.md).
- **Idempotency keys on every money- and stock-moving write**, from Phase 3 rather than alongside the offline work. It prevents double-charging a customer on a double-tap *today*, and it is the entire mechanism the Phase 9 offline outbox is built on — meaning offline sync needs no separate API and no second server-side write path.

## Current State

Planning docs written; **no application code yet**. Pick up at [`docs/ROADMAP.md`](docs/ROADMAP.md) → Phase 0.

Phase 0 is blocked on prerequisites that need a human: this machine has Node 24, pnpm and git, but **no .NET SDK, no Docker and no WSL2**. `wsl --install` requires a reboot and Docker Desktop's first run is interactive. See [Phase 0.1](docs/phases/PHASE-0-foundation.md).
