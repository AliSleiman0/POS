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
- **Refresh token in an `httpOnly` cookie** — deferred to [Phase 8.2](docs/phases/PHASE-8-deployment.md) along with hosting, because the right answer depends on the topology that phase picks. See the Phase 4.2 entry below for what ships until then.

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
