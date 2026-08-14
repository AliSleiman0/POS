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

- ~~**Hosting provider**~~ → **closed 2026-08-10: Fly.io.** See below.
- ~~**Payment processor**~~ → **closed 2026-07-31: there isn't one.** The product takes cash only; see [Payments](#feature-roadmap-phased) above. Not "Stripe, later" — no processor is planned at all.
- ~~**Refresh token in an `httpOnly` cookie**~~ → **closed 2026-08-10: it stays in `sessionStorage`.** See below.

### Resolved 2026-08-11 (during Phase 9)

- **A sale rung offline carries its own timestamp.** `POST /sales` accepts an optional
  `occurredAt`, and `Sale` gains a server-set `RecordedAt` beside `CompletedAt`.

  Without it, a sale queued at 22:00 and replayed at 09:00 is dated when the network came
  back. Every report in the system groups by `CompletedAt` — the trading-day bounds, the
  Z-report, a shift's takings — so an evening's trade would appear in the following day's
  figures and be missing from a drawer that was counted before it arrived. There is no way to
  reconstruct it afterwards: the information simply is not on the row.

  **It is the only client-supplied value in the system that reaches a stored column**, and it
  is admitted with two guards. `OfflineSaleRules` bounds it — minutes ahead of the server for
  clock skew between two machines, days behind it for a bank-holiday weekend offline but not
  for a till whose battery died — and `RecordedAt` records the server's own clock regardless,
  so the two can always be told apart. Outside the bounds is `422` with a stable type, because
  the refusal is permanent and an outbox has to send it to a person rather than retry.

  Deliberately **not** validated against the shift's opening time: reconciliation re-files a
  refused sale into whatever drawer is open now, carrying its original `occurredAt`, and a rule
  forbidding that would make the review queue a dead end.

- **CLAUDE.md invariant 3 is amended: the client prices a cart when — and only when — the
  server cannot be reached.** `src/Pos.Web/src/lib/pricing/` is a TypeScript port of
  `Pos.Core.Pricing`, including a faithful reimplementation of .NET's `decimal`.

  **This is a real weakening of a rule that was right**, and it is worth being plain about
  why. The invariant says the server computes every total because two implementations of tax
  and discount rules will differ eventually, and the place a difference surfaces is a customer
  disputing a receipt at a counter. That reasoning still holds. What changed is the
  alternative: offline there is no `POST /sales/quote` to ask, so the choice is not between one
  engine and two. It is between two engines and a till that cannot take money when the line
  goes down — which is the failure Phase 9 exists to fix, and the most common reason small
  businesses reject a POS.

  **What makes it survivable is `tests/fixtures/pricing-conformance.json`**: 913 carts
  generated from the C# engine's own test corpus — both tax modes, the hand-worked cases and
  500 seeded random baskets per mode — asserted by `PricingConformanceTests` on one side and
  `pricing.conformance.test.ts` on the other. Amounts are compared as strings, **scale
  included**, because `12.97` and `12.9700` are the same number and not the same result. A
  change to either engine that is not a change to both turns one side red in CI.

  Falsified rather than assumed: six deliberate breaks, five caught by the corpus. The sixth —
  division rounding half away from zero instead of half to even — leaves all 913 carts
  identical, because an exact tie at the 28th decimal place needs a quotient that terminates
  precisely there and a ratio of two four-decimal money amounts never does. It is caught one
  layer down by `decimal.test.ts`, whose expectations were read off the real .NET type. The
  corpus proves the engines agree on carts a shop can ring; the unit tests prove the primitives
  agree everywhere else. **Neither claim covers the other**, and the limit is recorded in both
  files rather than left to be discovered.

  The online path is unchanged. `POST /sales/quote` stays authoritative whenever it is
  reachable, and the server still prices the sale that is finally written.

- **The outbox is durable; the cart and the credential are not.** CLAUDE.md invariant 11 says
  the register survives a reload and credentials do not, and sends both the cart and the
  refresh token to `sessionStorage` so a shared till hands the next shift nothing. Phase 9's
  handoff asked for this to be reconciled explicitly rather than quietly widened, so:

  | | Where | Survives a browser close? |
  |---|---|---|
  | Cart | `sessionStorage` | **No** — the next shift must not inherit a basket |
  | Refresh token | `sessionStorage` | **No** — invariant 11, untouched |
  | Catalog mirror | IndexedDB | Yes — public shop data; rebuilding costs a download |
  | Outbox | IndexedDB | **Yes** — a queued sale is money that has already changed hands |

  **A queued sale is a third category, and that is the whole argument.** Losing a basket on a
  tab close is the correct direction to fail — a cashier re-scans. Losing a sale a customer has
  already paid for is not, and no amount of shared-device hygiene makes it so. Nothing in
  IndexedDB is a credential, and the database is keyed per tenant, so a tablet signed into a
  second shop can neither read the first's prices nor replay its queue.

  **The cost is accepted and stated in the app.** A till restarted while offline cannot sign in
  — the refresh token died with the tab and a PIN is verified by the server — so it cannot
  trade until the connection returns. Its queue is kept and sent then. Caching PIN hashes
  locally would fix that and would remove server-side lockout and rate limiting, which is a
  larger security change than the capability is worth.

### Resolved 2026-08-10 (during Phase 8)

- **Hosting is Render.** Three resources in one `render.yaml`: the API as a Docker web
  service, the SPA as a static site, and a managed Postgres.

  **Fly.io was chosen first and reversed the same day, for a reason that is about us rather
  than the technology.** Fly requires a payment card before it will create anything, and the
  owner is not adding one at this stage. That is a legitimate constraint and it decides the
  question — Render is the only credible option that starts with no card (Railway now gives a
  one-off $5 trial and then $1/month, which does not cover an app *and* a database).

  **The two limitations are real and are accepted with open eyes**, not glossed:

  - A **free web service sleeps after ~15 minutes idle** and takes about a minute to wake.
    For a till with a customer at the counter that is disqualifying — it is precisely the
    failure this product exists to avoid, and it was why the Fly config pinned a machine
    running. It is fine for proving a deployment and running Phase 8's verification.
    **Moving the API to a paid instance is the single change that makes this
    production-viable**, and it is the first thing to do before a real shop uses it.
  - A **free Postgres is deleted 30 days after creation** (with a 14-day grace period). The
    restore drill against it is still real and worth doing, but "backups proven to restore"
    means less against a database that removes itself next month.

  Almost nothing was provider-specific, which is why the switch cost an hour: the Dockerfile,
  CORS, the RLS readiness check, the onboarding command, the runbook and every test are
  unchanged. What moved was `fly.toml` → `render.yaml`, the deploy pipeline's CLI calls →
  deploy hooks, and the SPA's headers from a Caddyfile into the blueprint.

  **The web app is deliberately not served by the API.** They have different scaling and
  caching characteristics, and serving the SPA from Kestrel means a CSS change restarts the
  backend — a cashier mid-sale pays for a frontend deploy. On Render this also means the SPA
  sits on a CDN that does *not* sleep, so only the first API call pays the cold start. The
  cost of splitting them is that every call is cross-origin, which is what forced the two
  decisions below.

- **The Render deployment is a DEVELOPMENT environment, not production.** Decided
  2026-08-11 after Phase 8 was verified on it. The free plan stays, with all three of its
  consequences accepted deliberately rather than tolerated silently:

  - The API **sleeps after ~15 minutes** and takes about a minute to wake. Unacceptable for a
    till with a customer at the counter, fine for a demo and for verification. **Upgrading the
    API instance is the single change that makes this production-viable** — it is not a
    rewrite, and that is why the limitation is affordable.
  - The database is **deleted 30 days after creation** (2026-09-10 for the current one) and
    has **no automatic backups**. The `pg_dump` procedure in the runbook is the backup.
  - Credentials on it are treated as development credentials. One `pos_app` password was
    exposed in a session transcript and rotated; the exposure was judged acceptable on those
    grounds rather than escalated.

  **What this defers rather than settles:** the moment a real shop's sales are on this, all
  three become production problems on the same day. The trigger to revisit is a paying client,
  not a date.

- **`SentryScrubber` does not scrub exception messages, and that is accepted for now.** It
  strips headers, request bodies, query strings, tags and extras — but an exception *message*
  travels as-is, and Npgsql puts the whole connection string into one. That is how a password
  reached a transcript during Phase 8.

  Accepted because no Sentry DSN is configured, so nothing is being sent anywhere: the SDK is
  inert without one. **The trigger is the DSN.** Wiring real error tracking to this deployment
  without closing the gap would send a live database credential to a third party on the first
  connection failure, which is the one failure guaranteed to happen eventually.

- **`pos_app` keeps `DELETE` on `sale`, `sale_line`, `tender` and `stock_movement`.** Invariant
  4 calls those append-only, and the Phase 7.0 revoke covered `audit_entry` only. Accepted:
  the rule is enforced by application code and by `No_route_updates_or_deletes_a_sale`, which
  enumerates the routing table, so there is no path to a delete. The grant is a missing second
  layer rather than an open door — the same shape of defence-in-depth that RLS provides for
  tenancy, and worth adding as a deliberate migration rather than a hurried one.

- **A Postgres connection string is accepted in either shape.** Managed hosts hand out
  `postgres://user:pass@host/db`; Npgsql speaks `Host=…;Database=…`. The two are not
  interchangeable and the failure is late and misleading — the configuration looks present and
  correct, the app starts, and the first query throws a parse error about a keyword named
  "postgres". `PostgresConnectionString.Normalize` converts at the one place every entry point
  goes through, so the API, the seeder and the onboarding command all take whatever the
  platform gave rather than each documenting a hand conversion done under time pressure.

- **The refresh token stays in `sessionStorage`; the access token stays in memory.** Deferred
  from Phase 4.2 to here because the answer depended on the topology, and the topology is now
  known: the web app is on its own origin, so the `httpOnly` cookie version would need
  `SameSite=None; Secure`, credentialed CORS with an exact origin, and a CSRF story that
  same-origin would not have needed.

  That is real work with a real surface, and the thing it protects against — an injected
  script reading the token — is better addressed at this stage by the CSP the web app now
  ships: `connect-src` names the API and nothing else, so a script that can read the token
  cannot send it anywhere. Combined with what was already true (rotation on every use, and
  reuse of a rotated token revoking the whole family), a stolen refresh token is usable until
  its owner next refreshes and the theft then logs both parties out loudly.

  Not closed forever — it is the obvious next hardening step if this ever handles more than
  cash — but it is closed as a Phase 8 question rather than deferred a third time.

- **CORS is an exact allow-list, and an empty one in Production refuses to boot.** A CORS
  mistake is invisible from the server: the API answers every probe healthily while the
  browser blocks every call, and the only evidence is a console message on a device in a
  shop. Same reasoning as `JwtOptions.ValidateOnStart` — fail at boot, where somebody is
  watching.

- **Error tracking is Sentry, and the request body never leaves the process.** The scrubber
  drops bodies wholesale rather than filtering fields, because a `POST /sales` body is a
  customer's basket and what they paid, and no field-level rule survives a schema that changes
  every phase — the failure mode of the one that does not is silent.

- **Structured logging is the built-in JSON console formatter, not Serilog.**
  `Microsoft.Extensions.Logging` already does structured logging and `NuGetAudit` runs at
  level `low`, which makes every package a standing liability. Serilog's main advantage
  (request timing) is better served by the metrics. Sentry is the only dependency the
  observability work added.

- **The application-level rate limiter caps credential guessing and nothing else.** There is
  deliberately no global request limiter. Its number would have to sit above whatever the
  busiest real shop does at its busiest minute — a figure nobody has measured, because no shop
  has used this yet — and every estimate that turns out low takes a till down mid-queue.
  Volumetric protection is the edge's job, where it can be tuned from observed traffic.

  The login limit is partitioned per source address and set for **a shop, not a person**:
  behind NAT an entire staff shares one address.

- **Row-level security is verified continuously, by the readiness probe.** Managed Postgres
  hands out a superuser by default, and an application connected as one has every RLS policy
  inert while `pg_policies` still lists them as enabled. A machine whose connection can bypass
  RLS reports unhealthy, receives no traffic, and fails the deploy. Chosen over a boot-time
  assertion because that answers "was the role right when this machine last started", which
  after six weeks of uptime is a claim about history.

### Resolved 2026-08-09 (during Phase 7)

- **Audit entries are *staged* on the shared scoped `DbContext`, not saved by the audit log.**
  `IAuditLog.Record` adds an entry and returns; whatever transaction the audited action is
  already running is what commits it. That is what makes "an entry cannot exist for work that
  rolled back, nor be missing for work that succeeded" true by construction rather than by
  every call site remembering to be careful — the sale writer's `onCommitting` callbacks, the
  stock endpoint's explicit transaction and the plain-`SaveChanges` endpoints all get it for
  free, because `AppDbContext` is scoped and every writer resolves the same one.

  The one place with no transaction to join is a *refusal*, which is why
  `RecordStandaloneAsync` exists as a separately named method rather than an overload. Staging
  and forgetting to save is then only reachable by calling `Record` in a handler that never
  saves, which the per-action tests catch immediately.

  The hazard worth knowing about: `SaleWriter` and `StockLedger` run inside
  `CreateExecutionStrategy().ExecuteAsync`, and a transient-failure retry replays the block with
  the change tracker still holding the previous attempt's additions — so `onCommitting` fires
  twice. The idempotency record has a unique index that catches its own version of this loudly;
  an audit entry has nothing, so `AuditLog` de-duplicates against its own tracked set.

- **`audit_entry` is append-only at the database, not by convention.** The migration revokes
  `UPDATE` and `DELETE` on it from `pos_app`. Every other append-only table here —
  `stock_movement`, `sale` — relies on no code path existing that would rewrite it, which is a
  real guarantee and not this one: an audit log's value is that the people it records cannot
  edit it.

  This is **not** free, and the trap is worth stating: the Phase 1.6 RLS migration runs
  `ALTER DEFAULT PRIVILEGES … GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO pos_app` for the
  role that runs every later migration, so the table was created *with* both. Falsified by
  removing the revoke — four tests went red and the `UPDATE` genuinely succeeded. Any future
  table that needs a privilege withheld must revoke it explicitly.

- **Employee creation sets an initial password; there is no email invite.** No mail
  infrastructure exists, and adding one to send a single message is a dependency, a deliverability
  problem and a secret to manage. The owner sets a password and reads it out. A forced-change
  flow was considered and dropped as its own feature: it needs a screen, a route and an auth
  state, and none of that is Phase 7's job.

- **A deactivated user's access token is not revoked.** Deactivation revokes their refresh
  tokens, and `IsActive` is checked at login, at PIN entry and on `/auth/me` — but not while
  validating a JWT, so an already-issued access token keeps working for up to ~15 minutes.
  Closing that needs either a per-request liveness read on every authenticated call or a
  token-version claim with somewhere to store the version. Both are real designs; neither is
  worth taking on to shorten a window that ends by itself. **Stated in `docs/API.md` rather than
  pretended away** — an owner removing somebody after an incident should know the session does
  not die instantly.

- **The authorization matrix is derived from the policy map, and cannot check the policy map.**
  `AuthorizationMatrixTests` crosses the routing table with `PolicyCatalog`, which makes it
  exhaustive by construction — a route mapped tomorrow is covered the day it ships. What it
  cannot catch is an endpoint declaring the *wrong* policy, because the expectation is read off
  the endpoint's own metadata: verified by moving `GET /audit` from `CanManageEmployees` to
  `CanSell`, which left all 229 cases green. That question is answered by the `Refused` lists in
  `IsolationManifest` and by hand-written tests for the choices that matter, both of which did go
  red. Worth knowing before trusting the matrix for something it does not do.

### Resolved 2026-08-07 (during Phase 6.1)

- **`InvariantGlobalization` is off.** It was `true` from Phase 0 — the ASP.NET template's
  default, carried in without a decision — and it is incompatible with what this product is.
  Every tenant carries an IANA `TimeZoneId`, and under invariant globalization Windows cannot
  resolve one at all (`TimeZoneNotFoundException` on `"Europe/Dublin"`) while Linux still reads
  its own tz files and succeeds. That split is worse than either answer on its own: receipts and
  trading-day reports would have passed on the CI runner and failed on every developer machine,
  which is the failure mode that survives longest.

  It was found the way these things are found — the first receipt test returned 500 — and it had
  been latent since Phase 0 because nothing before 6.1 ever converted a timestamp out of UTC.
  Invariant 8 ("UTC everywhere, business day at the edge") has depended on ICU since it was
  written; the build was simply not honouring it yet.

  The usual objection to turning it on is that culture-sensitive string comparisons become
  possible. That is not a new risk here: `AnalysisLevel` is `latest-recommended` with warnings as
  errors, so an implicit-culture comparison already fails the build, and the codebase passes
  `StringComparison.Ordinal` or `CultureInfo.InvariantCulture` explicitly throughout. The full
  suite was green either side of the change.

- **The time zone reaches `Pos.Core` as a `TimeZoneInfo`, never as an id to look up.**
  `FindSystemTimeZoneById` reads the OS zone database, which is file access, and invariant 1
  keeps that out of the domain layer. `Pos.Api/Common/TenantTimeZone.Resolve` does the lookup and
  hands the zone in. The side benefit is the one that matters day to day: `ReceiptBuilder` and
  Phase 6.3's business-day arithmetic can be tested against a zone a test invents, instead of
  depending on what the machine running the suite happens to have installed.

  An unknown id **throws** rather than falling back to UTC. A silent fallback prints a receipt an
  hour out and puts a late sale on the wrong trading day — both look entirely plausible on paper,
  and neither would ever be reported as a bug.

- **~~Receipt reprints are marked by the client (Phase 6.2).~~ Superseded in Phase 7.2 — the
  server counts issues.** <a id="receipt-reprints-are-marked-by-the-client-phase-62"></a>

  6.2 left the mark to whoever was rendering, and said plainly why: counting copies means an
  append-only record of each issue, which is the shape of the audit log, and building a private
  one-off version first meant writing it twice. The stated limitation was that a client which
  chose not to send the mark would print an unmarked duplicate — a refund-fraud vector.

  **7.2 closed it.** `GET /sales/{id}/receipt` appends a `ReceiptIssued` entry and derives
  `isReprint`/`issueNumber` from the count, so the mark is now the server's answer and the
  client displays it. The two alternatives 6.2 rejected are still rejected: a print counter on
  `Sale` would break invariant 4, and there is no `POST` because a till pressing "print" is a
  read of the payload.

  **The cost is that a `GET` writes**, which 6.2 correctly named as the objection. It is
  accepted on a narrower reading of what is being recorded: not "this was printed" but "this was
  *disclosed*", and opening the preview does disclose it. So a cashier who looks without
  printing marks the next copy as a reprint — erring toward marking, which is the safe direction
  for a fraud control. Two simultaneous requests can both call themselves the first; a unique
  index would refuse to print a receipt a customer is waiting for, which is the worse failure,
  and both are recorded either way.

### Resolved 2026-08-02 (during Phase 4)

- **The refresh token lives in `sessionStorage`; the access token lives in memory only.**

  The alternative is an `httpOnly` cookie, and it is genuinely safer against XSS. It is deferred
  rather than rejected, for a reason that is about sequencing rather than security: the API
  returns both tokens in the login body today, and the cookie version's shape depends entirely
  on the deployment topology. Phase 8.2 puts the web app on a static host/CDN and the API in a
  container — *cross-origin* — which needs `SameSite=None; Secure`, credentialed CORS, and a
  CSRF story that same-origin would not. Building that now would be guessing at 8.2's answer,
  and guessing wrong means writing it twice.

  `sessionStorage` rather than `localStorage` is the part that is decided rather than deferred:
  both are readable by an injected script, but `sessionStorage` dies with the tab, so a shared
  counter tablet holds no usable refresh token once the browser is closed. The cost is a login
  after a reboot, which is the right direction to fail for a device sitting on a shop counter.

  What actually limits the damage is server-side and already built: refresh tokens rotate on
  every use and reuse of a rotated one revokes the whole family. A stolen refresh token is
  usable only until its owner next refreshes, and the theft then logs both parties out — loudly,
  rather than granting quiet indefinite access.

- **Concurrent 401s share one refresh.** Not an optimisation. Rotation plus revoke-on-reuse
  means five parallel refreshes rotate once and replay a spent token four times, which the
  server correctly reads as a leak — so the naive version signs the cashier out mid-sale. One
  module-level promise, and `refresh.test.ts` holds five callers open at once to prove it.

- **An expired session prompts *over* the current screen; it does not navigate.** The obvious
  implementation redirects to `/login`, which unmounts the route tree and takes Phase 5's cart
  with it. So the session status distinguishes `anonymous` (never signed in → navigate) from
  `expired` (was signed in → modal on top, screen untouched). Built in Phase 4, where there is
  nothing to lose yet, precisely so Phase 5 inherits it working.

- **Every endpoint handler returns a `Results<…>` union, never a bare `IResult`.** A contract
  requirement, not a style preference: OpenAPI infers the response schema from the declared
  return type, and `Task<IResult>` produces an operation with no response content — which the
  generated TypeScript client types as `never`. All five `/auth` handlers were like this, so the
  two calls every client makes first were untyped. `ResponseSchemaContractTests` fails the build
  on a regression.

- **`Idempotency-Key` is declared in the OpenAPI document by an operation transformer**, driven
  off the same `IdempotentEndpointMetadata` marker that attaches the filter. The header is read
  by an endpoint filter rather than bound as a parameter, so nothing told OpenAPI it existed and
  the generated client had no typed way to send it. `IdempotencyDocumentTests` asserts the two
  lists agree in both directions.

- **The OpenAPI document is served in Testing as well as Development, and never in Production.**
  Testing so a contract test can read the real document rather than a rebuilt approximation —
  an approximation would have agreed with itself and missed both gaps above. Not Production: it
  is anonymous by necessity and enumerates every route and schema, which is free reconnaissance
  for no benefit. `OpenApiRoutingTests` pins all three environments.

- **A last-login timestamp is written set-based, not through the change tracker.** `LastLoginAt`
  went through `UserManager.UpdateAsync`, which checks Identity's `ConcurrencyStamp` and returns
  a *failed result rather than throwing*. Two simultaneous logins for one account left the user
  entity `Modified` in the tracker, and the next `SaveChangesAsync` — inserting the refresh
  token — threw. The login failed with a 500 having already verified the password. A convenience
  timestamp must never be able to fail a login, so it is an `ExecuteUpdateAsync` that names the
  row and carries no concurrency token. Found by the Playwright suite running workers in
  parallel; pinned by `ConcurrentLoginTests`.

### Resolved 2026-08-02 (during Phase 3.8)

- **The close changes the shift's status in the same statement that locks the row** —
  `UPDATE shift SET status='Closed' WHERE … AND status='Open' RETURNING id`. There is no window
  between checking and setting, so a second close finds no `Open` row and gets nothing back.

- **A sale takes `FOR SHARE` on the shift; the close takes the exclusive lock.** Many sales may
  hold the share lock at once — they do not conflict with each other — and what they conflict
  with is the close. Either the sale commits before the close reads the drawer, or it blocks,
  finds the shift closed and is refused 409. **No sale is ever counted-then-refused or
  committed-but-uncounted**, which is the third outcome
  `A_sale_committing_while_a_shift_closes_is_either_counted_or_refused` exists to rule out.

- **Voided sales are excluded from expected cash, not netted off.** A void hands the cash
  straight back, so it never stayed in the drawer. Netting would give the same total while
  making the report claim takings that did not happen.

- **Expected cash counts `tendered − change given`, not the tendered note.** A €20 note against
  an €18.45 sale leaves €18.45 in the drawer. Counting the note overstates the day by the change
  handed back on every sale — falsified, and it reddened three tests.

- **Only `Cash` tenders count toward the drawer.** An `External` terminal's takings reconcile
  against that terminal, not against this drawer.

- **`ShiftWriter` is not behind a Core port**, unlike `IStockLedger` and `ISaleWriter`. There is
  no rule here Core needs to own: the arithmetic is already pure in `ShiftArithmetic`, and what
  remains is three queries and a lock. It is public in `Pos.Data` because `Pos.Api` references
  that project and its endpoints already use `AppDbContext` directly — a port would exist only
  to hide a type from a project allowed to see it.

- **Closing is `CanCloseShift`; recording a drop is `CanSell`.** A drop to the safe mid-shift is
  done by whoever is on the till, often the only person in the shop. A cashier who could close
  their own drawer could also decide what it was supposed to contain.

- **A cash movement against a closed shift is refused.** Its expected cash is already computed
  and stored, so a later movement would leave a variance that no longer explains the drawer.

### Resolved 2026-08-02 (during Phase 3.7)

- **A refund is re-priced from the original sale line's snapshots**, never from the catalog.
  The customer is owed what they paid. Falsified by pricing from `Product.UnitPrice` instead,
  which refunds today's price and looks entirely correct on the receipt.

- **A line discount comes back in proportion to the quantity returned.** One of three items
  with €0.60 off the line returns €0.20 of it. The share is computed at full precision and
  rounded once with everything else — rounding a per-unit figure and multiplying it back up is
  the per-line rounding bug wearing a different hat.

- **Voided refunds do not count against the remaining refundable quantity.** A voided refund
  put the goods back on the customer's side of the counter, so counting it would refuse a
  refund they never received. Falsified.

- **A sale with a live refund against it cannot be voided** — voiding writes compensating
  movements for every line, and a refund has already returned some of them, so doing both
  returns the same goods twice. The rule is deliberately type-agnostic, which is what lets a
  *refund* be voided by the same code path.

- **Two refunds of the same units in one refund cost a cent less than two separate refunds**,
  and that is correct rather than a bug. Two units in one refund is 2 × 1.2000 = 2.4000 net,
  +23% = 2.9520 → €2.95; two separate one-unit refunds are €1.48 each → €2.96. Each refund is
  its own amount a person is handed, rounded once. Asserting €2.96 for the combined case would
  have been asserting per-line rounding.

- **The original sale is locked `FOR UPDATE` while a refund is computed**, so two concurrent
  refunds cannot each see the same "two remaining" and each pay out two.

- **A replayed void returns a small acknowledgement, not the whole sale.** The stored body has
  to be written inside the transaction, before the sale can be read back in its final state, so
  storing a full snapshot there would risk it disagreeing with the row. An honest small
  response beats a large one that might be wrong.

- **`No_route_updates_or_deletes_a_sale` enumerates the routing table** rather than grepping.
  A `PUT`, `PATCH` or `DELETE` under `/sales` would be a way to rewrite a financial record in
  place, and the test stays true as routes are added.

### Resolved 2026-08-02 (during Phase 3.6)

- **`ISaleWriter` is the second port Core declares, and the precedent stays narrow.** It earns
  one on the same grounds `IStockLedger` did: committing a sale is a transaction containing a
  row lock, a counter increment, a concurrency token and a batch append, not a save. The
  reading endpoints still use `AppDbContext` directly.

- **The sale number comes from a counter row upserted inside the sale's transaction**, not a
  Postgres sequence and not `MAX()+1`. A sequence advances even when the transaction that drew
  from it rolls back, so a failed sale would burn a number permanently — and gaps in a
  financial series look like deleted records to an auditor with no way to prove otherwise.
  `ON CONFLICT (tenant_id) DO UPDATE … RETURNING` takes a row-level exclusive lock, so a
  concurrent sale blocks and then reads the committed value. Issued through ADO rather than
  EF's `SqlQuery`, which composes its argument into a subquery where Postgres will not accept a
  data-modifying statement. Cost, stated so it is not discovered under load: concurrent sales
  *within one tenant* serialise on that row for the length of the sale transaction.

- **The cashier is taken from the validated token, not the request.** `SaleCommitRequest` has
  nowhere to put one. A caller able to supply it could attribute a sale — and a price
  override — to a colleague, with nothing on the row to say otherwise. Same rule as the
  ledger's `PerformedBy`.

- **A manager authorises one action with a single-use grant, not a session swap** (Phase 5.3).
  A cashier cannot discount a line, and the two obvious ways to let a manager permit it are both
  wrong. Swapping the session makes the sale the manager's, so the drawer's Z-report reconciles
  the wrong person; granting the role temporarily is a standing permission with no defined end.
  Instead `POST /auth/override` takes the manager's PIN at the enrolled till and returns an
  opaque grant, stored hashed in `override_grant`, that `POST /sales` presents once and consumes
  inside the sale's transaction. The cashier stays signed in, the sale stays theirs, and
  `SaleLine.OverriddenBy` names the manager.

  Two alternatives were weighed and rejected. A **short-lived signed JWT** with a policy claim
  needs no table and half the code — but it authorises a *window* rather than an action, so one
  PIN would discount every sale in the next two minutes, which is precisely the fraud the flow
  exists to prevent. A **manager PIN session held in memory** for the one call needs no backend
  change at all, and mis-attributes the sale as above.

  Consumption is deliberately placed *inside* the writer's transaction rather than before or
  after it: before, a sale that then failed validation would leave the cashier needing the
  manager back for a second PIN; after, a crash in between would leave the grant spendable again.

- **The sale's GUID lives in the cart, not in the submit** (Phase 5.4). It is minted when
  *tendering begins* rather than when Complete is pressed, so every attempt from that moment
  carries the same key and a retry after a lost response returns the original sale. A key created
  inside the mutation would be new each time, which makes the `Idempotency-Key` header decorative
  and charges the customer twice for one basket. In the cart reducer specifically — unlike the
  manager's override grant, which is a credential and is deliberately kept out — because Phase 5.5
  persists the in-flight sale to `sessionStorage`, and "the GUID and the cart" is then one thing to
  serialise rather than two.

  The e2e test for it is not a double click: the submit button disables itself while a request is
  in flight, so a double click proves the courtesy works and says nothing about the mechanism. The
  test lets the first request reach the server and throws its response away, which is the failure
  the key exists for.

- **A till that lost the answer to a sale asks the server what happened; it does not re-submit to
  find out** (Phase 5.5). `GET /sales/by-client-transaction/{id}` exists for exactly one caller: a
  register that reloaded during `POST /sales` and comes back holding the GUID it sent and nothing
  else.

  Re-POSTing would answer the same question, and idempotency would make it safe *in the case where
  the sale landed*. It is the other case that rules it out: if the request never arrived, the retry
  **creates the sale** — a charge nobody authorised, on a page load, with the customer possibly
  already gone. A page load is not a person pressing Complete, and the difference is the whole
  reason for the endpoint.

  There are **three** answers, and the third is the one that needed designing. Taken, not taken,
  and *cannot be determined* — the server is unreachable. That last one gets a banner of its own
  telling the cashier not to ring it up again, and keeps the in-flight record so the question can
  be put again later. Collapsing it into "it failed" charges the customer twice; collapsing it into
  "it succeeded" gives the goods away. Neither guess is better than saying so.

- **Persisting the cart is not the same feature as recovering a payment, and it was cheaper to do
  both** (Phase 5.5). The exit criterion needed only the in-flight sale in `sessionStorage`; what
  ships writes the whole cart on every change. An accidental F5 twenty items into a shop otherwise
  costs a minute of a queue's time, and the sale's GUID rides along for free because it already
  lives in the cart.

  Signing out clears it; a session expiring does not. An expiry is the same person still serving
  the same customer — `RequireAuth` renders over the tree rather than unmounting it, deliberately,
  since Phase 4.2 — whereas signing out at a shared till means the next person, and handing them
  the last one's basket is how the wrong items get sold.

- **`override-not-permitted` reveals that somebody is not a manager, and that is the better
  trade.** `GET /employees/pin-eligible` deliberately withholds roles so a list readable from a
  counter does not become the shop's org chart, and this `403` weakens that. It only does so for
  a caller who has **already typed that person's correct PIN**, so it is not an oracle anyone at
  the counter can query. The alternative — answering `invalid-credentials` — tells a manager
  their own correct PIN is wrong, and they retry until the account locks.

- **A shift is checked twice, and the second check is not redundant.** The endpoint reads it
  unlocked to produce a good error message and to draw three distinctions: an unknown or
  cross-tenant shift is `400` on the field, a shift belonging to another register is `400` on
  `registerId`, and a genuinely closed one is `409`. The writer then re-checks it under
  `FOR SHARE` inside the transaction, which is where a shift closing *concurrently* is decided.
  **Deleting the writer's check left the entire API suite green** — the endpoint masks it under
  sequential conditions — so `SaleWriterTests` in `Pos.Data.Tests` exists to test the writer's
  guards with no endpoint in front of them.

- **The idempotency record is enlisted through a callback the writer invokes inside its
  transaction.** `Pos.Data` cannot reference the API's idempotency types, and a host with no
  HTTP has no key to record, so the alternative — a Core port for an HTTP concern — would have
  been worse. `A_failure_inside_the_callback_rolls_the_whole_sale_back` forces the atomicity
  claim rather than assuming it.

- **A discrepancy is written when the on-hand ends up below zero, not when stock "looked
  insufficient".** Selling the last three of three lands on zero and is an ordinary sale;
  flagging it would bury the real oversells in noise. Insufficient stock never blocks a sale —
  the customer is standing at the counter holding the item.

- **`SalesFixture` moved from 3.9 into 3.6.** `GET /sales` and `GET /stock/discrepancies` are
  collection endpoints, and the isolation manifest requires `Expected`/`Forbidden` id lists in
  both tenants the moment they exist. The alternative was a dishonest `Exempt` row.

### Resolved 2026-08-01 (during Phase 3.5)

- **Both `IdempotencyRecord` and `sale.client_transaction_id` exist, because they guarantee
  different things.** The unique index on the sale is the *domain* guarantee — "exactly one
  sale for this cart" survives even if the idempotency table were dropped, and it is what
  Phase 9's outbox reconciles against. `IdempotencyRecord` is the *transport* guarantee: it
  stores the original status and body so a replay is byte-identical, and it covers the 🔒
  routes that create no sale at all (stock adjustments, shift open and close, cash movements)
  plus a void, which mutates a sale rather than inserting one.

- **The uniqueness is on `(tenant_id, key)` and deliberately excludes the endpoint.** Reusing
  one key on two endpoints is the same client bug as reusing it with two bodies and earns the
  same 409. The endpoint is inside the request hash instead, which is what makes it a mismatch
  rather than a silent second success.

- **The fingerprint is over the raw request bytes, not a re-serialised DTO.** A client that
  changed a field the server currently ignores has still changed the request, and hashing the
  bound object would call that a replay. It would also make every stored key depend on
  serializer settings — turning on camelCase would 409 every in-flight retry in every shop.

- **Endpoint filters run after model binding, so the request body must be buffered.** Without
  `EnableBuffering()` the filter reads zero bytes, every fingerprint matches, and a retry
  replays a stored response for an unrelated request. The filter therefore *refuses to guess*:
  it throws when the body is not seekable rather than hashing nothing, which converts a silent
  catastrophe into an immediate error. Verified by deleting the middleware and watching the
  suite go red.

- **A concurrent loser asks "is this request already done?", not "which index did I lose on".**
  The first implementation matched specific constraint names and returned a 500 the moment two
  *first* receipts of a brand-new product collided on `ux_stock_item_tenant_product` instead —
  a race neither index in the list covered. The filter now catches any unique violation,
  re-reads the key, and replays if it is present. One re-read suffices with no retry loop,
  because the losing INSERT blocks until the winner commits, so the winner's row is already
  visible when the 23505 arrives.

- **A replay reproduces status and body, not headers.** A replayed `201` carries no `Location`.
  Storing arbitrary headers to reproduce one value that a retrying client already holds would
  be a column nothing reads.

- **A missing or malformed `Idempotency-Key` is a `400` with a field error, not a `428`.**
  Every other malformed request in this API answers that way, and a client that must branch on
  428 for one endpoint is a client that will not.

### Resolved 2026-08-01 (during Phase 3.1)

- **`Money` is an EF-mapped value type on entities, and stays out of API DTOs.** A
  `readonly record struct Money(decimal Amount)` in `Pos.Core`, mapped model-wide by a value
  converter, so `Product.UnitPrice` and every Phase 3 amount column is typed `Money` rather
  than `decimal`. Request and response DTOs stay `decimal`, so the JSON contract and Phase 4's
  generated TypeScript client are untouched by the change.

  The enforcement this buys is specific: there is **no `operator +(Money, decimal)`**, so
  `total + 1.005m` does not compile and raw decimal arithmetic on a price has to be written as
  an explicit cast that shows up in a diff. That is CLAUDE.md invariant 3 moved from review
  discipline into the compiler. Converting the two `Product` columns immediately surfaced
  every boundary in the codebase as a compile error — which is the mechanism working, not a
  cost of it.

- **The namespace is `Pos.Core.Monetary`, not `Pos.Core.Money`.** The phase doc says
  `Pos.Core/Money/Money.cs`, and that does not compile for consumers. A namespace `Pos.Core.Money`
  makes `Money` a *member of `Pos.Core`*, and enclosing-namespace members outrank types imported
  by a `using` — so every file under `Pos.Core.*` writing `Money` gets **CS0118: 'Money' is a
  namespace but is used like a type**, which is the whole domain layer. Verified by trying it.
  The alternatives were qualifying every usage as `Money.Money` or dropping the type into the
  `Pos.Core` root and breaking the folder↔namespace convention every other folder follows.
  Renaming the folder is the cheapest of the three and costs nothing at the call site.

- **The `Money` value converter does not round.** Rounding on write would put a rounding rule
  in a layer nobody reads. Writers call `RoundToStorage()` explicitly before `SaveChanges`, so
  the decision is visible where it is taken. Postgres would silently round a fifth decimal
  place anyway; keeping the converter dumb is what makes `CatalogRules.IsStorable*` the thing
  that prevents it rather than a second line of defence nobody can see.

- **Recorded cost: EF cannot aggregate over a value-converted property.**
  `db.Tenders.SumAsync(t => t.Amount.Amount)` does not translate, so 3.8's shift arithmetic
  reads its aggregates through `db.Database.SqlQuery<decimal>` — one statement per component,
  with the `tenant_id` predicate written **explicitly** because the query filter does not
  compose over raw SQL, and RLS underneath as the second layer. This is the strongest argument
  available against typing entity amounts as `Money`, so it is written down rather than
  discovered. Pinned by `MoneyMappingTests.Summing_money_in_the_database_is_not_translatable`,
  which fails if a future EF version gains the ability — at which point the workaround can go.

- **`CatalogRules` was kept, not folded into `Money`.** It validates a tax *rate* and a signed
  *quantity*, and neither of those is money — folding them in would make a kilogram a currency.
  What they now share is the `Rounding` primitive, so the repository holds exactly one
  `MidpointRounding` constant and one `decimal.Round` call site. `CatalogRulesTests` passed
  **unedited** through the extraction, which is what makes "behaviour-preserving" a claim
  somebody checked rather than asserted.

- **`ArchitectureTests.Core_does_not_read_the_ambient_clock` is a real Mono.Cecil IL scan.**
  The placeholder's own comment named Phase 3, and 3.1 is where Core first gained logic whose
  determinism is the product. `Mono.Cecil` is referenced by `tests/Pos.Core.Tests` only, so the
  two checks that Core declares and compiles against nothing outside the BCL are unaffected.
  `TimeProvider.System` is on the forbidden list alongside `DateTime.UtcNow` and friends —
  reaching the clock *through* `TimeProvider` is the same sin and merely looks compliant.
  Falsified in both directions before being trusted: a planted `DateTime.UtcNow` and a planted
  `TimeProvider.System` **inside a lambda** were both caught, the second proving the walk into
  compiler-generated nested types is load-bearing.

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

**Phases 0–5 are complete.** A cashier can open the drawer, scan, adjust and discount a cart the
server prices — with a manager's PIN authorising what they cannot approve alone — and take cash for
it, exactly once, with change read off the server's figure. **Exactly once now holds across a
reload**: the till comes back with the basket and the sale's GUID, and resolves an interrupted
payment by asking the server what that GUID bought rather than submitting it again.
877 .NET tests, 159 Vitest and 35 Playwright specs — the last against a real API and a real
Postgres, not mocks.

One Phase 5 exit criterion is deliberately unmet: the receipt action on the completion panel. There
is nothing to print until `GET /sales/{id}/receipt` exists, so the panel says so rather than
stubbing it, and 6.1 builds both halves.

Pick up at [`docs/ROADMAP.md`](docs/ROADMAP.md) → Phase 6.1, and read
[`docs/HANDOFF.md`](docs/HANDOFF.md) first for session state.
