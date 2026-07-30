# POS Project — Locked-In Decisions

This file is the source of truth for architecture/product decisions made before any code was written. Read this first at the start of the next session.

## Product

- **What we're building**: a POS (point of sale) system, aiming to be flexible enough for both retail and restaurant workflows ("universal"), though retail is the build-first target.
- **Business model**: sell as a whole product, not a per-seat license. Leaning toward one-time purchase, but this is now in tension with the hosting decision below (see "Open question" section) — needs to be resolved deliberately, since it affects pricing.
- **Sequencing**: **web (online) version first**, desktop version later, sharing the same backend.

## Hosting & Multi-tenancy

- **We (the vendor) host the web version** — SaaS-style. All clients' data lives on our infrastructure.
- This means the backend must be **multi-tenant** from the start: every table/entity scoped by `TenantId`, with strict data isolation between clients.

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
- **Database**: multi-tenant relational DB (Postgres or SQL Server — not yet decided) behind the API, accessed via EF Core.

## Solution Structure (planned)

```
YourPOS.sln
 ├─ YourPOS.Core     — business logic (pricing, tax, discounts, order rules). Pure C#, no DB/UI dependencies.
 ├─ YourPOS.Data      — EF Core: entities, DbContext, multi-tenant-aware repositories.
 ├─ YourPOS.Api        — ASP.NET Core Web API. The one backend both web and (later) desktop clients call.
 ├─ YourPOS.Web        — React/TS PWA frontend, talks to Api.
 └─ YourPOS.Desktop   — (later) Avalonia app. Talks to the SAME Api; adds a local offline cache/sync layer rather than being a fully separate offline silo.
```

Rationale: `Core` and `Data` are shared, unmodified, between web and desktop — this is what makes "same backend, two clients" real rather than aspirational. Only the UI/hosting layer differs per client.

## Admin / Roles

Two distinct "admin" concepts — do not conflate them:

1. **Store admin/owner role** (inside each tenant's POS) — role-based access control (`Cashier`, `Manager`, `Owner`) distinguishing who can change prices, view margins, issue refunds, manage employees, etc. **This is core MVP functionality**, not optional — build it from the start as part of `Core`/`Api`.
2. **Platform admin** (for us, the vendor) — a separate internal tool/dashboard to manage all tenants: onboarding, billing/payment status, active/inactive status, impersonate-for-support. **Decision: not worth building yet.** Build it right after the first paying client, once real operational needs are known. Before that, direct DB inspection is an acceptable stopgap for a handful of tenants.

## Feature Roadmap (phased)

1. **MVP — retail core**: product catalog, barcode/manual entry, cart, checkout (cash + card), receipt printing, inventory decrement, daily sales report.
2. **V2 — restaurant mode**: tables/tabs, kitchen printer routing, item modifiers, split bills, tipping. Retail and restaurant have genuinely different data models (simple "sale" vs. "order" with courses/seats/modifiers) — do not force one schema for both prematurely; extend once retail is proven.
3. **V3 — business layer**: employee accounts/roles (see Admin section), customer/loyalty, purchase orders, low-stock alerts, multi-register on one tenant.
4. **V4 — nice-to-haves**: gift cards, analytics dashboard, platform admin (see above), license/activation considerations if the one-time-sale model is kept.

## Open Questions (unresolved — revisit next session)

- **Pricing model conflict**: "sell as a whole, not a license" (one-time) vs. "we host it" (implies ongoing infra cost). Needs a deliberate decision — e.g. one-time fee that includes lifetime hosting, or accept a recurring fee without calling it a "license."
- Postgres vs. SQL Server for the multi-tenant database.
- Multi-tenant isolation strategy: shared DB with `TenantId` column vs. database-per-tenant vs. schema-per-tenant.
- Payment processor choice (Stripe Terminal, Square, SumUp, etc.) — affects both web and eventual desktop peripheral integration.

## Not Yet Started

No code has been written yet. Next session should pick up at: resolving the pricing/hosting tension above, then scaffolding the solution structure and designing the multi-tenant data model.
